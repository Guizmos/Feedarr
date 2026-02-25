using System.Text.Json;
using System.Text.Json.Serialization;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services;
using System.Diagnostics;

namespace Feedarr.Api.Services.Jikan;

public sealed class JikanClient
{
    public sealed record AnimeResult(
        int MalId,
        string Title,
        int? Year,
        string? ImageUrl,
        string? Synopsis,
        string? Genres,
        string? Url,
        double? Rating);

    private readonly HttpClient _http;
    private readonly ActiveExternalProviderConfigResolver _resolver;
    private readonly ProviderStatsService _stats;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public JikanClient(
        HttpClient http,
        ActiveExternalProviderConfigResolver resolver,
        ProviderStatsService stats)
    {
        _http = http;
        _resolver = resolver;
        _stats = stats;
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<bool> TestApiAsync(CancellationToken ct)
    {
        var active = _resolver.Resolve(ExternalProviderKeys.Jikan);
        if (!active.Enabled)
            return false;

        var endpoint = BuildEndpoint("anime?q=naruto&limit=1");
        using var resp = await GetTrackedAsync(endpoint, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<AnimeResult?> SearchAnimeAsync(string title, int? year, CancellationToken ct)
    {
        var active = _resolver.Resolve(ExternalProviderKeys.Jikan);
        if (!active.Enabled)
            return null;

        var query = (title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var endpoint = BuildEndpoint($"anime?q={Uri.EscapeDataString(query)}&limit=10");
        using var resp = await GetTrackedAsync(endpoint, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var payload = await JsonSerializer.DeserializeAsync<JikanSearchResponse>(stream, JsonOpts, ct);
        if (payload?.Data is null || payload.Data.Count == 0)
            return null;

        var best = payload.Data
            .Select(item => new
            {
                Item = item,
                Score = ComputeMatchScore(query, year, item)
            })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Item.ScoredBy ?? 0)
            .FirstOrDefault()?.Item;

        if (best is null || best.MalId <= 0)
            return null;

        var imageUrl = best.Images?.Jpg?.LargeImageUrl
            ?? best.Images?.Jpg?.ImageUrl
            ?? best.Images?.Webp?.LargeImageUrl
            ?? best.Images?.Webp?.ImageUrl;

        var genres = best.Genres is null || best.Genres.Count == 0
            ? null
            : string.Join(", ", best.Genres.Select(g => g.Name?.Trim()).Where(g => !string.IsNullOrWhiteSpace(g)));

        return new AnimeResult(
            best.MalId,
            (best.Title ?? best.TitleEnglish ?? "").Trim(),
            best.Year,
            imageUrl,
            best.Synopsis?.Trim(),
            genres,
            best.Url?.Trim(),
            best.Score > 0 ? best.Score : null);
    }

    public async Task<byte[]?> DownloadImageAsync(string? url, CancellationToken ct)
    {
        var normalized = NormalizeAbsoluteUrl(url);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        using var resp = await GetTrackedAsync(normalized, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    private string BuildEndpoint(string relative)
    {
        var active = _resolver.Resolve(ExternalProviderKeys.Jikan);
        var rawBase = string.IsNullOrWhiteSpace(active.BaseUrl)
            ? "https://api.jikan.moe/v4/"
            : active.BaseUrl!;
        var baseUri = EnsureTrailingSlash(rawBase);
        var relativePath = relative.TrimStart('/');
        return new Uri(baseUri, relativePath).ToString();
    }

    private async Task<HttpResponseMessage> GetTrackedAsync(string endpoint, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await _http.GetAsync(endpoint, ct);
            _stats.RecordExternal(ExternalProviderKeys.Jikan, resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);
            return resp;
        }
        catch
        {
            _stats.RecordExternal(ExternalProviderKeys.Jikan, ok: false, sw.ElapsedMilliseconds);
            throw;
        }
    }

    private static Uri EnsureTrailingSlash(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            uri = new Uri("https://api.jikan.moe/v4/");

        var normalized = uri.ToString();
        if (!normalized.EndsWith("/", StringComparison.Ordinal))
            normalized += "/";
        return new Uri(normalized, UriKind.Absolute);
    }

    private static string? NormalizeAbsoluteUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var raw = input.Trim();
        if (raw.StartsWith("//", StringComparison.Ordinal))
            raw = "https:" + raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return null;

        return uri.ToString();
    }

    private static int ComputeMatchScore(string query, int? year, JikanItem item)
    {
        var score = 0;
        var normalizedQuery = query.Trim().ToLowerInvariant();

        var titles = new[]
        {
            item.Title,
            item.TitleEnglish
        }.Concat(item.TitleSynonyms ?? Enumerable.Empty<string>())
         .Where(static t => !string.IsNullOrWhiteSpace(t))
         .Select(static t => t!.Trim().ToLowerInvariant())
         .Distinct()
         .ToList();

        if (titles.Any(t => t == normalizedQuery))
            score += 5;
        else if (titles.Any(t => t.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)))
            score += 3;
        else if (titles.Any(t => normalizedQuery.Contains(t, StringComparison.OrdinalIgnoreCase)))
            score += 2;

        if (year.HasValue && item.Year.HasValue && item.Year.Value == year.Value)
            score += 2;

        if (!string.IsNullOrWhiteSpace(item.Images?.Jpg?.LargeImageUrl)
            || !string.IsNullOrWhiteSpace(item.Images?.Jpg?.ImageUrl)
            || !string.IsNullOrWhiteSpace(item.Images?.Webp?.LargeImageUrl)
            || !string.IsNullOrWhiteSpace(item.Images?.Webp?.ImageUrl))
            score += 1;

        return score;
    }

    private sealed class JikanSearchResponse
    {
        [JsonPropertyName("data")]
        public List<JikanItem>? Data { get; set; }
    }

    private sealed class JikanItem
    {
        [JsonPropertyName("mal_id")]
        public int MalId { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("title_english")]
        public string? TitleEnglish { get; set; }

        [JsonPropertyName("title_synonyms")]
        public List<string>? TitleSynonyms { get; set; }

        [JsonPropertyName("year")]
        public int? Year { get; set; }

        [JsonPropertyName("synopsis")]
        public string? Synopsis { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("score")]
        public double? Score { get; set; }

        [JsonPropertyName("scored_by")]
        public int? ScoredBy { get; set; }

        [JsonPropertyName("images")]
        public JikanImages? Images { get; set; }

        [JsonPropertyName("genres")]
        public List<JikanGenre>? Genres { get; set; }
    }

    private sealed class JikanImages
    {
        [JsonPropertyName("jpg")]
        public JikanImageSet? Jpg { get; set; }

        [JsonPropertyName("webp")]
        public JikanImageSet? Webp { get; set; }
    }

    private sealed class JikanImageSet
    {
        [JsonPropertyName("image_url")]
        public string? ImageUrl { get; set; }

        [JsonPropertyName("large_image_url")]
        public string? LargeImageUrl { get; set; }
    }

    private sealed class JikanGenre
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
