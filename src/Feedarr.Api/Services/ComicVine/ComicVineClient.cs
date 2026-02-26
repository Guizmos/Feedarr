using System.Text.Json;
using System.Text.Json.Serialization;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services;
using System.Diagnostics;

namespace Feedarr.Api.Services.ComicVine;

public sealed class ComicVineClient
{
    private const string UserAgent = "Feedarr/1.0";

    public sealed record ComicResult(
        string ProviderId,
        string Title,
        string? Description,
        string? CoverUrl,
        string? ReleaseDate,
        string? InfoUrl);

    private readonly HttpClient _http;
    private readonly ActiveExternalProviderConfigResolver _resolver;
    private readonly ProviderStatsService _stats;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ComicVineClient(
        HttpClient http,
        ActiveExternalProviderConfigResolver resolver,
        ProviderStatsService stats)
    {
        _http = http;
        _resolver = resolver;
        _stats = stats;
        _http.Timeout = TimeSpan.FromSeconds(20);
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    public async Task<bool> TestApiAsync(CancellationToken ct)
    {
        var apiKey = ResolveCreds();
        if (string.IsNullOrWhiteSpace(apiKey))
            return false;

        var endpoint = BuildSearchEndpoint(apiKey, "batman");
        using var resp = await GetTrackedAsync(endpoint, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<ComicResult?> SearchComicAsync(string title, int? year, CancellationToken ct)
    {
        var apiKey = ResolveCreds();
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        var query = (title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var endpoint = BuildSearchEndpoint(apiKey, query);
        using var resp = await GetTrackedAsync(endpoint, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var payload = await JsonSerializer.DeserializeAsync<ComicVineSearchResponse>(stream, JsonOpts, ct);
        if (payload?.Results is null || payload.Results.Count == 0)
            return null;

        var best = payload.Results
            .Where(item => item.Id > 0 && !string.IsNullOrWhiteSpace(item.Name))
            .OrderByDescending(item => MatchScore(query, year, item))
            .FirstOrDefault();
        if (best is null)
            return null;

        var providerId = best.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new ComicResult(
            providerId,
            best.Name!.Trim(),
            StripHtml(best.Description ?? best.Deck),
            NormalizeAbsoluteUrl(best.Image?.OriginalUrl ?? best.Image?.SmallUrl),
            best.CoverDate?.Trim(),
            best.SiteDetailUrl?.Trim());
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

    private string? ResolveCreds()
    {
        var active = _resolver.Resolve(ExternalProviderKeys.ComicVine);
        if (!active.Enabled)
            return null;

        if (!active.Auth.TryGetValue("apiKey", out var apiKey))
            return null;

        var key = (apiKey ?? "").Trim();
        if (string.IsNullOrWhiteSpace(key))
            return null;

        return key;
    }

    private string BuildSearchEndpoint(string apiKey, string query)
    {
        var active = _resolver.Resolve(ExternalProviderKeys.ComicVine);
        var rawBase = string.IsNullOrWhiteSpace(active.BaseUrl)
            ? "https://comicvine.gamespot.com/api/"
            : active.BaseUrl!;
        if (!Uri.TryCreate(rawBase, UriKind.Absolute, out var baseUri))
            baseUri = new Uri("https://comicvine.gamespot.com/api/");

        var normalized = baseUri.ToString();
        if (!normalized.EndsWith("/", StringComparison.Ordinal))
            normalized += "/";
        var root = new Uri(normalized, UriKind.Absolute);

        var relative = EnsureFormatJson($"search/?api_key={Uri.EscapeDataString(apiKey)}&resources=issue,volume&limit=10&query={Uri.EscapeDataString(query)}");
        return new Uri(root, relative).ToString();
    }

    private static string EnsureFormatJson(string relativePath)
    {
        var relative = relativePath ?? "";
        var separator = relative.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        if (relative.Contains("format=", StringComparison.OrdinalIgnoreCase))
            return relative;
        return relative + separator + "format=json";
    }

    private static int MatchScore(string query, int? year, ComicVineResult item)
    {
        var score = 0;
        var normalizedQuery = query.Trim().ToLowerInvariant();
        var candidate = (item.Name ?? "").Trim().ToLowerInvariant();

        if (candidate == normalizedQuery) score += 5;
        else if (candidate.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)) score += 3;
        else if (normalizedQuery.Contains(candidate, StringComparison.OrdinalIgnoreCase)) score += 2;

        var yearCandidate = ExtractYear(item.CoverDate) ?? item.StartYear;
        if (year.HasValue && yearCandidate.HasValue && yearCandidate.Value == year.Value)
            score += 2;

        if (!string.IsNullOrWhiteSpace(item.Image?.OriginalUrl) || !string.IsNullOrWhiteSpace(item.Image?.SmallUrl))
            score += 1;

        return score;
    }

    private static int? ExtractYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4)
            return null;
        return int.TryParse(date.AsSpan(0, 4), out var year) ? year : null;
    }

    private static string? NormalizeAbsoluteUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var normalized = raw.Trim();
        if (normalized.StartsWith("//", StringComparison.Ordinal))
            normalized = "https:" + normalized;

        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            ? uri.ToString()
            : null;
    }

    private static string? StripHtml(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var text = System.Text.RegularExpressions.Regex.Replace(input, "<.*?>", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private async Task<HttpResponseMessage> GetTrackedAsync(string endpoint, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            req.Headers.UserAgent.ParseAdd(UserAgent);
            var resp = await _http.SendAsync(req, ct);
            _stats.RecordExternal(ExternalProviderKeys.ComicVine, resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);
            return resp;
        }
        catch
        {
            _stats.RecordExternal(ExternalProviderKeys.ComicVine, ok: false, sw.ElapsedMilliseconds);
            throw;
        }
    }

    private sealed class ComicVineSearchResponse
    {
        [JsonPropertyName("results")]
        public List<ComicVineResult>? Results { get; set; }
    }

    private sealed class ComicVineResult
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("deck")]
        public string? Deck { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("cover_date")]
        public string? CoverDate { get; set; }

        [JsonPropertyName("start_year")]
        public int? StartYear { get; set; }

        [JsonPropertyName("site_detail_url")]
        public string? SiteDetailUrl { get; set; }

        [JsonPropertyName("image")]
        public ComicVineImage? Image { get; set; }
    }

    private sealed class ComicVineImage
    {
        [JsonPropertyName("original_url")]
        public string? OriginalUrl { get; set; }

        [JsonPropertyName("small_url")]
        public string? SmallUrl { get; set; }
    }
}
