using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Feedarr.Api.Services;

namespace Feedarr.Api.Services.ExternalProviders;

public sealed class ExternalProviderTestService
{
    private const string ComicVineUserAgent = "Feedarr/1.0";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ProviderStatsService _stats;
    private readonly ILogger<ExternalProviderTestService> _logger;

    public ExternalProviderTestService(
        IHttpClientFactory httpClientFactory,
        ProviderStatsService stats,
        ILogger<ExternalProviderTestService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _stats = stats;
        _logger = logger;
    }

    public async Task<ExternalProviderTestOutcome> TestAsync(
        string providerKey,
        string? baseUrl,
        IReadOnlyDictionary<string, string?> auth,
        CancellationToken ct)
    {
        var key = (providerKey ?? "").Trim().ToLowerInvariant();
        var sw = Stopwatch.StartNew();

        try
        {
            if (key == ExternalProviderKeys.ComicVine)
            {
                var comicVineResult = await TestComicVineAsync(baseUrl, auth, ct);
                return new ExternalProviderTestOutcome(
                    comicVineResult.Ok,
                    sw.ElapsedMilliseconds,
                    comicVineResult.Error ?? (comicVineResult.Ok ? null : "provider test failed"));
            }

            var ok = key switch
            {
                ExternalProviderKeys.Tmdb => await TestTmdbAsync(baseUrl, auth, ct),
                ExternalProviderKeys.Tvmaze => await TestTvMazeAsync(baseUrl, ct),
                ExternalProviderKeys.Fanart => await TestFanartAsync(baseUrl, auth, ct),
                ExternalProviderKeys.Igdb => await TestIgdbAsync(auth, ct),
                ExternalProviderKeys.Jikan => await TestJikanAsync(baseUrl, ct),
                ExternalProviderKeys.GoogleBooks => await TestGoogleBooksAsync(baseUrl, auth, ct),
                ExternalProviderKeys.TheAudioDb => await TestTheAudioDbAsync(baseUrl, auth, ct),
                ExternalProviderKeys.MusicBrainz => await TestMusicBrainzAsync(baseUrl, auth, ct),
                ExternalProviderKeys.OpenLibrary => await TestOpenLibraryAsync(baseUrl, ct),
                ExternalProviderKeys.Rawg => await TestRawgAsync(baseUrl, auth, ct),
                _ => false
            };

            return new ExternalProviderTestOutcome(ok, sw.ElapsedMilliseconds, ok ? null : "provider test failed");
        }
        catch (TaskCanceledException)
        {
            return new ExternalProviderTestOutcome(false, sw.ElapsedMilliseconds, "timeout");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "External provider instance test failed for providerKey={ProviderKey}", key);
            return new ExternalProviderTestOutcome(false, sw.ElapsedMilliseconds, "upstream provider unavailable");
        }
    }

    private async Task<bool> TestTmdbAsync(string? baseUrl, IReadOnlyDictionary<string, string?> auth, CancellationToken ct)
    {
        var apiKey = GetAuthValue(auth, "apiKey");
        if (string.IsNullOrWhiteSpace(apiKey))
            return false;

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        var baseUri = BuildBaseUri(baseUrl, "https://api.themoviedb.org/3/");
        var url = new Uri(baseUri, $"configuration?api_key={Uri.EscapeDataString(apiKey)}");

        var sw = Stopwatch.StartNew();
        using var resp = await client.GetAsync(url, ct);
        _stats.RecordTmdb(resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);
        return resp.IsSuccessStatusCode;
    }

    private async Task<bool> TestTvMazeAsync(string? baseUrl, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        var baseUri = BuildBaseUri(baseUrl, "https://api.tvmaze.com/");
        var url = new Uri(baseUri, "search/shows?q=the");

        var sw = Stopwatch.StartNew();
        using var resp = await client.GetAsync(url, ct);
        _stats.RecordTvmaze(resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);
        return resp.IsSuccessStatusCode;
    }

    private async Task<bool> TestFanartAsync(string? baseUrl, IReadOnlyDictionary<string, string?> auth, CancellationToken ct)
    {
        var apiKey = GetAuthValue(auth, "apiKey");
        if (string.IsNullOrWhiteSpace(apiKey))
            return false;

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        var baseUri = BuildBaseUri(baseUrl, "https://webservice.fanart.tv/v3/");
        var url = new Uri(baseUri, $"movies/550?api_key={Uri.EscapeDataString(apiKey)}");

        var sw = Stopwatch.StartNew();
        using var resp = await client.GetAsync(url, ct);
        _stats.RecordFanart(resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);
        return resp.IsSuccessStatusCode;
    }

    private async Task<bool> TestIgdbAsync(IReadOnlyDictionary<string, string?> auth, CancellationToken ct)
    {
        var clientId = GetAuthValue(auth, "clientId");
        var clientSecret = GetAuthValue(auth, "clientSecret");
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return false;

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(25);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "client_credentials"
        });

        var sw = Stopwatch.StartNew();
        using var resp = await client.PostAsync("https://id.twitch.tv/oauth2/token", form, ct);
        _stats.RecordIgdb(resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);
        if (!resp.IsSuccessStatusCode)
            return false;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.TryGetProperty("access_token", out var token)
            && token.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(token.GetString());
    }

    private async Task<bool> TestJikanAsync(string? baseUrl, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        var baseUri = BuildBaseUri(baseUrl, "https://api.jikan.moe/v4/");
        var url = new Uri(baseUri, "anime?q=naruto&limit=1");

        var sw = Stopwatch.StartNew();
        using var resp = await client.GetAsync(url, ct);
        _stats.RecordExternal(ExternalProviderKeys.Jikan, resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);
        return resp.IsSuccessStatusCode;
    }

    private async Task<bool> TestGoogleBooksAsync(string? baseUrl, IReadOnlyDictionary<string, string?> auth, CancellationToken ct)
    {
        var apiKey = GetAuthValue(auth, "apiKey");
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        var baseUri = BuildBaseUri(baseUrl, "https://www.googleapis.com/books/v1/");

        var relative = $"volumes?q={Uri.EscapeDataString("intitle:harry potter")}&maxResults=1";
        if (!string.IsNullOrWhiteSpace(apiKey))
            relative += $"&key={Uri.EscapeDataString(apiKey)}";

        var url = new Uri(baseUri, relative);
        var sw = Stopwatch.StartNew();
        using var resp = await client.GetAsync(url, ct);
        _stats.RecordExternal(ExternalProviderKeys.GoogleBooks, resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);
        return resp.IsSuccessStatusCode;
    }

    private async Task<bool> TestTheAudioDbAsync(string? baseUrl, IReadOnlyDictionary<string, string?> auth, CancellationToken ct)
    {
        var apiKey = GetAuthValue(auth, "apiKey");
        if (string.IsNullOrWhiteSpace(apiKey))
            return false;

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        var baseUri = BuildBaseUri(baseUrl, "https://www.theaudiodb.com/api/v1/json/");
        var relative = $"{apiKey.Trim().Trim('/')}/searchalbum.php?s=Daft%20Punk&a=Random%20Access%20Memories";
        var url = new Uri(baseUri, relative);

        var sw = Stopwatch.StartNew();
        using var resp = await client.GetAsync(url, ct);
        _stats.RecordExternal(ExternalProviderKeys.TheAudioDb, resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);
        return resp.IsSuccessStatusCode;
    }

    private async Task<bool> TestOpenLibraryAsync(string? baseUrl, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        var baseUri = BuildBaseUri(baseUrl, "https://openlibrary.org/");
        var url = new Uri(baseUri, "search.json?title=Harry+Potter&limit=1&fields=title,cover_i");
        var sw = Stopwatch.StartNew();
        using var resp = await client.GetAsync(url, ct);
        _stats.RecordExternal(ExternalProviderKeys.OpenLibrary, resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);
        return resp.IsSuccessStatusCode;
    }

    private async Task<bool> TestMusicBrainzAsync(string? baseUrl, IReadOnlyDictionary<string, string?> auth, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        if (client.DefaultRequestHeaders.UserAgent.Count == 0)
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0 ( https://github.com/Guizmos/feedarr )");

        var baseUri = BuildBaseUri(baseUrl, "https://musicbrainz.org/ws/2/");
        var query = Uri.EscapeDataString("release:\"Abbey Road\" AND artist:\"The Beatles\"");
        var url = new Uri(baseUri, $"release/?query={query}&fmt=json&limit=1");

        var sw = Stopwatch.StartNew();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var clientId = GetAuthValue(auth, "clientId");
        if (!string.IsNullOrWhiteSpace(clientId))
            req.Headers.TryAddWithoutValidation("X-Application", clientId);

        using var resp = await client.SendAsync(req, ct);
        _stats.RecordExternal(ExternalProviderKeys.MusicBrainz, resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);
        return resp.IsSuccessStatusCode;
    }

    private async Task<bool> TestRawgAsync(string? baseUrl, IReadOnlyDictionary<string, string?> auth, CancellationToken ct)
    {
        var apiKey = GetAuthValue(auth, "apiKey");
        if (string.IsNullOrWhiteSpace(apiKey))
            return false;

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        var baseUri = BuildBaseUri(baseUrl, "https://api.rawg.io/api/");
        var url = new Uri(baseUri, $"games?key={Uri.EscapeDataString(apiKey)}&page_size=1&search=halo");

        var sw = Stopwatch.StartNew();
        using var resp = await client.GetAsync(url, ct);
        _stats.RecordExternal(ExternalProviderKeys.Rawg, resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);
        return resp.IsSuccessStatusCode;
    }

    private async Task<(bool Ok, string? Error)> TestComicVineAsync(string? baseUrl, IReadOnlyDictionary<string, string?> auth, CancellationToken ct)
    {
        var apiKey = GetAuthValue(auth, "apiKey");
        if (string.IsNullOrWhiteSpace(apiKey))
            return (false, "missing apiKey");

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        if (client.DefaultRequestHeaders.UserAgent.Count == 0)
            client.DefaultRequestHeaders.UserAgent.ParseAdd(ComicVineUserAgent);

        var baseUri = BuildBaseUri(baseUrl, "https://comicvine.gamespot.com/api/");
        var relative = $"search/?api_key={Uri.EscapeDataString(apiKey)}&format=json&query={Uri.EscapeDataString("Batman")}&resources=volume&limit=1";
        var url = new Uri(baseUri, relative);

        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(ComicVineUserAgent);

            using var resp = await client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if ((int)resp.StatusCode != 200)
            {
                _stats.RecordExternal(ExternalProviderKeys.ComicVine, ok: false, sw.ElapsedMilliseconds);
                return (false, $"HTTP {(int)resp.StatusCode}: {BodyExcerpt(body)}");
            }

            ComicVineTestResponse? payload;
            try
            {
                payload = JsonSerializer.Deserialize<ComicVineTestResponse>(body);
            }
            catch (JsonException)
            {
                _stats.RecordExternal(ExternalProviderKeys.ComicVine, ok: false, sw.ElapsedMilliseconds);
                return (false, $"invalid json response: {BodyExcerpt(body)}");
            }

            if (payload is null)
            {
                _stats.RecordExternal(ExternalProviderKeys.ComicVine, ok: false, sw.ElapsedMilliseconds);
                return (false, $"empty json response: {BodyExcerpt(body)}");
            }

            var statusCode = payload.StatusCode ?? 0;
            var error = (payload.Error ?? "").Trim();
            var ok = statusCode == 1
                     && (string.IsNullOrWhiteSpace(error)
                         || string.Equals(error, "OK", StringComparison.OrdinalIgnoreCase));

            _stats.RecordExternal(ExternalProviderKeys.ComicVine, ok, sw.ElapsedMilliseconds);

            if (!ok)
            {
                var errorLabel = string.IsNullOrWhiteSpace(error) ? "n/a" : error;
                return (false, $"ComicVine status_code={statusCode}, error={errorLabel}");
            }

            return (true, null);
        }
        catch
        {
            _stats.RecordExternal(ExternalProviderKeys.ComicVine, ok: false, sw.ElapsedMilliseconds);
            throw;
        }
    }

    private static string BodyExcerpt(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "empty body";
        var compact = body
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return compact.Length <= 220 ? compact : compact[..220] + "...";
    }

    private static string? GetAuthValue(IReadOnlyDictionary<string, string?> auth, string key)
    {
        if (!auth.TryGetValue(key, out var value))
            return null;

        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static Uri BuildBaseUri(string? input, string fallback)
    {
        var raw = string.IsNullOrWhiteSpace(input) ? fallback : input.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("invalid baseUrl");

        var normalized = uri.ToString();
        if (!normalized.EndsWith("/", StringComparison.Ordinal))
            normalized += "/";
        return new Uri(normalized, UriKind.Absolute);
    }
}

file sealed class ComicVineTestResponse
{
    [JsonPropertyName("status_code")]
    public int? StatusCode { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed record ExternalProviderTestOutcome(bool Ok, long ElapsedMs, string? Error);
