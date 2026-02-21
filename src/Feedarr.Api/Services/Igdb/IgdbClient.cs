using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services;

namespace Feedarr.Api.Services.Igdb;

public sealed class IgdbClient
{
    public sealed record DetailsResult(
        string? Title,
        string? Summary,
        string? ReleaseDate,
        double? Rating,
        int? Votes,
        string? Genres,
        string? Url);

    private static void ThrowIfRateLimited(HttpResponseMessage resp)
    {
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("IGDB rate limit exceeded", null, HttpStatusCode.TooManyRequests);
    }

    private readonly HttpClient _http;
    private readonly ProviderStatsService _stats;
    private readonly ActiveExternalProviderConfigResolver _activeConfigResolver;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private string? _token;
    private DateTimeOffset _tokenExpiresAt;
    private readonly SemaphoreSlim _tokenSemaphore = new(1, 1);

    public IgdbClient(
        HttpClient http,
        ProviderStatsService stats,
        ActiveExternalProviderConfigResolver activeConfigResolver)
    {
        _http = http;
        _stats = stats;
        _activeConfigResolver = activeConfigResolver;
        _http.Timeout = TimeSpan.FromSeconds(25);
    }

    private (string ClientId, string ClientSecret)? GetCreds()
    {
        var active = _activeConfigResolver.Resolve(ExternalProviderKeys.Igdb);
        if (!active.Enabled) return null;

        active.Auth.TryGetValue("clientId", out var activeClientId);
        active.Auth.TryGetValue("clientSecret", out var activeClientSecret);
        var resolvedClientId = (activeClientId ?? "").Trim();
        var resolvedClientSecret = (activeClientSecret ?? "").Trim();
        if (string.IsNullOrWhiteSpace(resolvedClientId) || string.IsNullOrWhiteSpace(resolvedClientSecret))
            return null;

        return (resolvedClientId, resolvedClientSecret);
    }

    private async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        // Fast path: check cached token without acquiring semaphore
        var cachedToken = Volatile.Read(ref _token);
        if (!string.IsNullOrWhiteSpace(cachedToken) && _tokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            return cachedToken;

        await _tokenSemaphore.WaitAsync(ct);
        try
        {
            // Double-check after acquiring semaphore
            if (!string.IsNullOrWhiteSpace(_token) && _tokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
                return _token;

            var creds = GetCreds();
            if (creds is null) return null;

            const string tokenUrl = "https://id.twitch.tv/oauth2/token";
            using var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = creds.Value.ClientId,
                ["client_secret"] = creds.Value.ClientSecret,
                ["grant_type"] = "client_credentials"
            });
            var recorded = false;
            var sw = Stopwatch.StartNew();

            try
            {
                using var resp = await _http.PostAsync(tokenUrl, tokenForm, ct);
                var ok = resp.IsSuccessStatusCode;
                _stats.RecordIgdb(ok, sw.ElapsedMilliseconds);
                recorded = true;
                if (!ok) return null;

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var data = await JsonSerializer.DeserializeAsync<TokenResponse>(stream, JsonOpts, ct);
                if (data is null || string.IsNullOrWhiteSpace(data.AccessToken)) return null;

                _token = data.AccessToken;
                _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, data.ExpiresIn));

                return _token;
            }
            catch
            {
                if (!recorded)
                    _stats.RecordIgdb(false, sw.ElapsedMilliseconds);
                throw;
            }
        }
        finally
        {
            _tokenSemaphore.Release();
        }
    }

    public async Task<bool> TestCredsAsync(CancellationToken ct)
    {
        var token = await GetTokenAsync(ct);
        return !string.IsNullOrWhiteSpace(token);
    }

    public async Task<(int igdbId, string coverUrl)?> SearchGameCoverAsync(string title, int? year, CancellationToken ct)
    {
        var creds = GetCreds();
        if (creds is null) return null;

        var token = await GetTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(token)) return null;

        title = CleanQuery(title);
        if (string.IsNullOrWhiteSpace(title)) return null;

        var where = "where cover != null";
        if (year is >= 1970 and <= 2100)
        {
            var start = new DateTimeOffset(new DateTime(year.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
            var end = new DateTimeOffset(new DateTime(year.Value + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
            where += $" & first_release_date >= {start} & first_release_date < {end}";
        }

        var body = $"search \"{EscapeIgdb(title)}\"; fields id,name,first_release_date,cover.url; {where}; limit 5;";

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games")
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        };
        req.Headers.Add("Client-ID", creds.Value.ClientId);
        req.Headers.Add("Authorization", $"Bearer {token}");

        var sw = Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, ct);
        var ok = resp.IsSuccessStatusCode;
        _stats.RecordIgdb(ok, sw.ElapsedMilliseconds);
        if (!ok) { ThrowIfRateLimited(resp); return null; }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var data = await JsonSerializer.DeserializeAsync<List<GameItem>>(stream, JsonOpts, ct);
        var best = data?.FirstOrDefault(x => x.Id > 0 && !string.IsNullOrWhiteSpace(x.Cover?.Url));
        if (best is null) return null;

        var url = NormalizeCoverUrl(best.Cover!.Url!);
        if (string.IsNullOrWhiteSpace(url)) return null;

        return (best.Id, url);
    }

    public async Task<List<(int igdbId, string title, int? year, string coverUrl)>> SearchGameListAsync(
        string title,
        int? year,
        CancellationToken ct)
    {
        var creds = GetCreds();
        if (creds is null) return new List<(int, string, int?, string)>();

        var token = await GetTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(token)) return new List<(int, string, int?, string)>();

        title = CleanQuery(title);
        if (string.IsNullOrWhiteSpace(title)) return new List<(int, string, int?, string)>();

        var where = "where cover != null";
        if (year is >= 1970 and <= 2100)
        {
            var start = new DateTimeOffset(new DateTime(year.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
            var end = new DateTimeOffset(new DateTime(year.Value + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
            where += $" & first_release_date >= {start} & first_release_date < {end}";
        }

        var body = $"search \"{EscapeIgdb(title)}\"; fields id,name,first_release_date,cover.url; {where}; limit 15;";

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games")
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        };
        req.Headers.Add("Client-ID", creds.Value.ClientId);
        req.Headers.Add("Authorization", $"Bearer {token}");

        var sw = Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, ct);
        var ok = resp.IsSuccessStatusCode;
        _stats.RecordIgdb(ok, sw.ElapsedMilliseconds);
        if (!ok) return new List<(int, string, int?, string)>();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var data = await JsonSerializer.DeserializeAsync<List<GameItem>>(stream, JsonOpts, ct);
        if (data is null || data.Count == 0) return new List<(int, string, int?, string)>();

        var results = new List<(int, string, int?, string)>();
        foreach (var item in data)
        {
            if (item.Id <= 0 || string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Cover?.Url))
                continue;

            var url = NormalizeCoverUrl(item.Cover!.Url!);
            if (string.IsNullOrWhiteSpace(url)) continue;

            int? releaseYear = null;
            if (item.FirstReleaseDate > 0)
            {
                var dt = DateTimeOffset.FromUnixTimeSeconds(item.FirstReleaseDate);
                releaseYear = dt.Year;
            }

            results.Add((item.Id, item.Name!, releaseYear, url));
        }

        return results;
    }

    public async Task<DetailsResult?> GetGameDetailsAsync(int igdbId, CancellationToken ct)
    {
        var creds = GetCreds();
        if (creds is null) return null;
        if (igdbId <= 0) return null;

        var token = await GetTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(token)) return null;

        var body = $"fields id,name,summary,first_release_date,genres.name,total_rating,total_rating_count,url; where id = {igdbId}; limit 1;";

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games")
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        };
        req.Headers.Add("Client-ID", creds.Value.ClientId);
        req.Headers.Add("Authorization", $"Bearer {token}");

        var sw = Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, ct);
        var ok = resp.IsSuccessStatusCode;
        _stats.RecordIgdb(ok, sw.ElapsedMilliseconds);
        if (!ok) { ThrowIfRateLimited(resp); return null; }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var data = await JsonSerializer.DeserializeAsync<List<GameDetailsItem>>(stream, JsonOpts, ct);
        var best = data?.FirstOrDefault(x => x.Id == igdbId);
        if (best is null) return null;

        string? releaseDate = null;
        if (best.FirstReleaseDate > 0)
        {
            var dt = DateTimeOffset.FromUnixTimeSeconds(best.FirstReleaseDate);
            releaseDate = dt.ToString("yyyy-MM-dd");
        }

        var genres = best.Genres is null || best.Genres.Count == 0
            ? null
            : string.Join(", ", best.Genres.Select(x => x.Name?.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)));

        return new DetailsResult(
            best.Name?.Trim(),
            best.Summary?.Trim(),
            releaseDate,
            best.TotalRating > 0 ? best.TotalRating : null,
            best.TotalRatingCount > 0 ? best.TotalRatingCount : null,
            genres,
            best.Url?.Trim()
        );
    }

    public async Task<byte[]?> DownloadCoverAsync(string url, CancellationToken ct)
    {
        var norm = NormalizeCoverUrl(url);
        if (string.IsNullOrWhiteSpace(norm)) return null;
        var recorded = false;
        var sw = Stopwatch.StartNew();

        try
        {
            using var resp = await _http.GetAsync(norm, ct);
            var ok = resp.IsSuccessStatusCode;
            _stats.RecordIgdb(ok, sw.ElapsedMilliseconds);
            recorded = true;
            if (!ok) resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch
        {
            if (!recorded)
                _stats.RecordIgdb(false, sw.ElapsedMilliseconds);
            throw;
        }
    }

    private static string NormalizeCoverUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var u = url.Trim();
        if (u.StartsWith("//")) u = "https:" + u;
        u = u.Replace("t_thumb", "t_cover_big", StringComparison.OrdinalIgnoreCase);
        return u;
    }

    private static string CleanQuery(string s)
    {
        s = (s ?? "").Trim();
        return s.Length > 200 ? s[..200] : s;
    }

    private static string EscapeIgdb(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }

    private sealed class GameItem
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("first_release_date")]
        public long FirstReleaseDate { get; set; }

        [JsonPropertyName("cover")]
        public CoverItem? Cover { get; set; }
    }

    private sealed class GameDetailsItem
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("first_release_date")]
        public long FirstReleaseDate { get; set; }

        [JsonPropertyName("genres")]
        public List<GenreItem>? Genres { get; set; }

        [JsonPropertyName("total_rating")]
        public double TotalRating { get; set; }

        [JsonPropertyName("total_rating_count")]
        public int TotalRatingCount { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    private sealed class CoverItem
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    private sealed class GenreItem
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
