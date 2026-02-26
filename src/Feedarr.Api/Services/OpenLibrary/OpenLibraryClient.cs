using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Feedarr.Api.Services.ExternalProviders;

namespace Feedarr.Api.Services.OpenLibrary;

public sealed class OpenLibraryClient
{
    public sealed record BookResult(
        string WorkId,
        string Title,
        string? Author,
        string? PublishedYear,
        string? CoverUrl);

    private const string OlBaseUrl = "https://openlibrary.org/";
    private const string CoverBaseUrl = "https://covers.openlibrary.org/";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly ProviderStatsService _stats;

    public OpenLibraryClient(HttpClient http, ProviderStatsService stats)
    {
        _http = http;
        _stats = stats;
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    public async Task<bool> TestApiAsync(CancellationToken ct)
    {
        var url = $"{OlBaseUrl}search.json?title=Harry+Potter&limit=1&fields=title,cover_i";
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            _stats.RecordExternal(ExternalProviderKeys.OpenLibrary, resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            _stats.RecordExternal(ExternalProviderKeys.OpenLibrary, false, sw.ElapsedMilliseconds);
            return false;
        }
    }

    /// <summary>
    /// Returns the best single match (for automatic poster matching).
    /// Prefers results that have cover art.
    /// </summary>
    public async Task<BookResult?> SearchBookAsync(string title, string? isbn, CancellationToken ct)
    {
        var results = await SearchBooksAsync(title, isbn, 5, ct);
        return results.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.CoverUrl))
            ?? results.FirstOrDefault();
    }

    /// <summary>
    /// Returns up to 10 results for the manual poster picker.
    /// </summary>
    public async Task<List<BookResult>> SearchBookListAsync(string title, CancellationToken ct)
    {
        return await SearchBooksAsync(title, null, 10, ct);
    }

    /// <summary>
    /// Downloads cover image bytes from a URL.
    /// </summary>
    public async Task<byte[]?> DownloadImageAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        try
        {
            var bytes = await _http.GetByteArrayAsync(url, ct);
            return bytes.Length == 0 ? null : bytes;
        }
        catch
        {
            return null;
        }
    }

    // ─── Internal helpers ─────────────────────────────────────────────────────

    private async Task<List<BookResult>> SearchBooksAsync(string title, string? isbn, int limit, CancellationToken ct)
    {
        var safeTitle = (title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(safeTitle) && string.IsNullOrWhiteSpace(isbn))
            return [];

        var fields = "key,title,author_name,first_publish_year,cover_i,isbn";
        string url;
        if (!string.IsNullOrWhiteSpace(isbn))
            url = $"{OlBaseUrl}search.json?isbn={Uri.EscapeDataString(isbn)}&limit={limit}&fields={fields}";
        else
            url = $"{OlBaseUrl}search.json?title={Uri.EscapeDataString(safeTitle)}&limit={limit}&fields={fields}";

        var sw = Stopwatch.StartNew();
        OlSearchResponse? response;
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            _stats.RecordExternal(ExternalProviderKeys.OpenLibrary, resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);

            if (!resp.IsSuccessStatusCode)
                return [];

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            response = await JsonSerializer.DeserializeAsync<OlSearchResponse>(stream, JsonOpts, ct);
        }
        catch
        {
            _stats.RecordExternal(ExternalProviderKeys.OpenLibrary, false, sw.ElapsedMilliseconds);
            return [];
        }

        if (response?.Docs is null || response.Docs.Count == 0)
            return [];

        var results = new List<BookResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in response.Docs)
        {
            var workId = (doc.Key ?? "").Trim().TrimStart('/');
            if (string.IsNullOrWhiteSpace(workId) || !seen.Add(workId))
                continue;

            var docTitle = doc.Title?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(docTitle))
                continue;

            var author = doc.AuthorName?.FirstOrDefault()?.Trim();
            var year = doc.FirstPublishYear?.ToString();
            var coverUrl = BuildCoverUrl(doc);

            results.Add(new BookResult(workId, docTitle, author, year, coverUrl));
        }

        return results;
    }

    private static string? BuildCoverUrl(OlDoc doc)
    {
        // Prefer cover_id-based URL (reliable)
        if (doc.CoverId is > 0)
            return $"{CoverBaseUrl}b/id/{doc.CoverId.Value}-L.jpg";

        // Fallback: ISBN-based cover
        var isbn = doc.Isbn?.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i));
        if (!string.IsNullOrWhiteSpace(isbn))
            return $"{CoverBaseUrl}b/isbn/{isbn.Trim()}-L.jpg";

        return null;
    }

    // ─── JSON models ──────────────────────────────────────────────────────────

    private sealed class OlSearchResponse
    {
        [JsonPropertyName("docs")]
        public List<OlDoc>? Docs { get; set; }
    }

    private sealed class OlDoc
    {
        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("author_name")]
        public List<string>? AuthorName { get; set; }

        [JsonPropertyName("first_publish_year")]
        public int? FirstPublishYear { get; set; }

        [JsonPropertyName("cover_i")]
        public long? CoverId { get; set; }

        [JsonPropertyName("isbn")]
        public List<string>? Isbn { get; set; }
    }
}
