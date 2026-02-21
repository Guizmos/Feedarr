using System.Text.Json;
using System.Text.Json.Serialization;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services;
using System.Diagnostics;

namespace Feedarr.Api.Services.GoogleBooks;

public sealed class GoogleBooksClient
{
    public sealed record BookResult(
        string VolumeId,
        string Title,
        string? Description,
        string? PublishedDate,
        string? Genres,
        double? Rating,
        int? RatingCount,
        string? ThumbnailUrl,
        string? InfoUrl);

    private readonly HttpClient _http;
    private readonly ActiveExternalProviderConfigResolver _resolver;
    private readonly ProviderStatsService _stats;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public GoogleBooksClient(
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
        var active = _resolver.Resolve(ExternalProviderKeys.GoogleBooks);
        if (!active.Enabled)
            return false;

        var endpoint = BuildVolumesEndpoint("intitle:harry potter", null);
        using var resp = await GetTrackedAsync(endpoint, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<BookResult?> SearchBookAsync(string title, string? isbn, CancellationToken ct)
    {
        var active = _resolver.Resolve(ExternalProviderKeys.GoogleBooks);
        if (!active.Enabled)
            return null;

        var trimmedTitle = (title ?? "").Trim();
        var trimmedIsbn = (isbn ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmedTitle) && string.IsNullOrWhiteSpace(trimmedIsbn))
            return null;

        var query = !string.IsNullOrWhiteSpace(trimmedIsbn)
            ? $"isbn:{trimmedIsbn}"
            : $"intitle:{trimmedTitle}";

        var endpoint = BuildVolumesEndpoint(query, active.Auth.TryGetValue("apiKey", out var apiKey) ? apiKey : null);
        using var resp = await GetTrackedAsync(endpoint, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var payload = await JsonSerializer.DeserializeAsync<GoogleBooksSearchResponse>(stream, JsonOpts, ct);
        if (payload?.Items is null || payload.Items.Count == 0)
            return null;

        var best = payload.Items
            .Where(item => item.VolumeInfo is not null && !string.IsNullOrWhiteSpace(item.VolumeInfo.Title))
            .OrderByDescending(item => MatchScore(trimmedTitle, trimmedIsbn, item))
            .ThenByDescending(item => item.VolumeInfo?.RatingsCount ?? 0)
            .FirstOrDefault();

        if (best?.VolumeInfo is null || string.IsNullOrWhiteSpace(best.Id))
            return null;

        var info = best.VolumeInfo;
        var genres = info.Categories is null || info.Categories.Count == 0
            ? null
            : string.Join(", ", info.Categories.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()));

        return new BookResult(
            best.Id.Trim(),
            info.Title!.Trim(),
            info.Description?.Trim(),
            info.PublishedDate?.Trim(),
            genres,
            info.AverageRating > 0 ? info.AverageRating : null,
            info.RatingsCount > 0 ? info.RatingsCount : null,
            NormalizeThumbnailUrl(info.ImageLinks?.Thumbnail ?? info.ImageLinks?.SmallThumbnail),
            info.InfoLink?.Trim());
    }

    public async Task<byte[]?> DownloadImageAsync(string? url, CancellationToken ct)
    {
        var normalized = NormalizeThumbnailUrl(url);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        using var resp = await GetTrackedAsync(normalized, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    private string BuildVolumesEndpoint(string q, string? apiKey)
    {
        var baseUri = GetBaseUri();
        var query = $"volumes?q={Uri.EscapeDataString(q)}&maxResults=10";
        var key = (apiKey ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(key))
            query += $"&key={Uri.EscapeDataString(key)}";
        return new Uri(baseUri, query).ToString();
    }

    private Uri GetBaseUri()
    {
        var active = _resolver.Resolve(ExternalProviderKeys.GoogleBooks);
        var rawBase = string.IsNullOrWhiteSpace(active.BaseUrl)
            ? "https://www.googleapis.com/books/v1/"
            : active.BaseUrl!;
        if (!Uri.TryCreate(rawBase, UriKind.Absolute, out var baseUri))
            baseUri = new Uri("https://www.googleapis.com/books/v1/");
        var normalized = baseUri.ToString();
        if (!normalized.EndsWith("/", StringComparison.Ordinal))
            normalized += "/";
        return new Uri(normalized, UriKind.Absolute);
    }

    private static string? NormalizeThumbnailUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var normalized = raw.Trim();
        if (normalized.StartsWith("//", StringComparison.Ordinal))
            normalized = "https:" + normalized;
        if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            normalized = "https://" + normalized["http://".Length..];

        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            ? uri.ToString()
            : null;
    }

    private static int MatchScore(string title, string isbn, GoogleBooksItem item)
    {
        var score = 0;
        var queryTitle = (title ?? "").Trim().ToLowerInvariant();
        var queryIsbn = (isbn ?? "").Trim().Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        var info = item.VolumeInfo;
        if (info is null)
            return score;

        var candidateTitle = (info.Title ?? "").Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(queryTitle))
        {
            if (candidateTitle == queryTitle) score += 5;
            else if (candidateTitle.Contains(queryTitle, StringComparison.OrdinalIgnoreCase)) score += 3;
            else if (queryTitle.Contains(candidateTitle, StringComparison.OrdinalIgnoreCase)) score += 2;
        }

        if (!string.IsNullOrWhiteSpace(queryIsbn) && info.IndustryIdentifiers is not null)
        {
            var hasIsbn = info.IndustryIdentifiers
                .Select(x => (x.Identifier ?? "").Replace("-", "", StringComparison.Ordinal).Trim().ToLowerInvariant())
                .Any(x => x == queryIsbn);
            if (hasIsbn) score += 6;
        }

        if (!string.IsNullOrWhiteSpace(info.ImageLinks?.Thumbnail) || !string.IsNullOrWhiteSpace(info.ImageLinks?.SmallThumbnail))
            score += 1;

        return score;
    }

    private async Task<HttpResponseMessage> GetTrackedAsync(string endpoint, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await _http.GetAsync(endpoint, ct);
            _stats.RecordExternal(ExternalProviderKeys.GoogleBooks, resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);
            return resp;
        }
        catch
        {
            _stats.RecordExternal(ExternalProviderKeys.GoogleBooks, ok: false, sw.ElapsedMilliseconds);
            throw;
        }
    }

    private sealed class GoogleBooksSearchResponse
    {
        [JsonPropertyName("items")]
        public List<GoogleBooksItem>? Items { get; set; }
    }

    private sealed class GoogleBooksItem
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("volumeInfo")]
        public GoogleVolumeInfo? VolumeInfo { get; set; }
    }

    private sealed class GoogleVolumeInfo
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("publishedDate")]
        public string? PublishedDate { get; set; }

        [JsonPropertyName("categories")]
        public List<string>? Categories { get; set; }

        [JsonPropertyName("averageRating")]
        public double AverageRating { get; set; }

        [JsonPropertyName("ratingsCount")]
        public int RatingsCount { get; set; }

        [JsonPropertyName("imageLinks")]
        public GoogleImageLinks? ImageLinks { get; set; }

        [JsonPropertyName("infoLink")]
        public string? InfoLink { get; set; }

        [JsonPropertyName("industryIdentifiers")]
        public List<GoogleIdentifier>? IndustryIdentifiers { get; set; }
    }

    private sealed class GoogleImageLinks
    {
        [JsonPropertyName("smallThumbnail")]
        public string? SmallThumbnail { get; set; }

        [JsonPropertyName("thumbnail")]
        public string? Thumbnail { get; set; }
    }

    private sealed class GoogleIdentifier
    {
        [JsonPropertyName("identifier")]
        public string? Identifier { get; set; }
    }
}
