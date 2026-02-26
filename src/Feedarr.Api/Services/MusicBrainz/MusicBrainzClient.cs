using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Feedarr.Api.Services.ExternalProviders;

namespace Feedarr.Api.Services.MusicBrainz;

public sealed class MusicBrainzClient
{
    public sealed record MusicResult(
        string Mbid,
        string Title,
        string? Artist,
        string? Released,
        string? CoverUrl);

    private const string MbBaseUrl = "https://musicbrainz.org/ws/2/";
    private const string CaaBaseUrl = "https://coverartarchive.org/";
    private const string UserAgent = "Feedarr/1.0 ( https://github.com/Guizmos/feedarr )";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly ActiveExternalProviderConfigResolver _resolver;
    private readonly ProviderStatsService _stats;

    public MusicBrainzClient(HttpClient http, ActiveExternalProviderConfigResolver resolver, ProviderStatsService stats)
    {
        _http = http;
        _resolver = resolver;
        _stats = stats;
        _http.Timeout = TimeSpan.FromSeconds(25);
        EnsureUserAgent(_http);
    }

    private (string? ClientId, string? ClientSecret) ResolveCreds()
    {
        var config = _resolver.Resolve(ExternalProviderKeys.MusicBrainz);
        if (!config.Enabled) return (null, null);

        config.Auth.TryGetValue("clientId", out var clientId);
        config.Auth.TryGetValue("clientSecret", out var clientSecret);

        return (
            string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim(),
            string.IsNullOrWhiteSpace(clientSecret) ? null : clientSecret.Trim()
        );
    }

    // ─── Public API ──────────────────────────────────────────────────────────

    public async Task<bool> TestApiAsync(CancellationToken ct)
    {
        // Search for "Abbey Road" by The Beatles as a connectivity check
        var url = BuildSearchUrl("Abbey Road", "The Beatles");
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            _stats.RecordExternal(ExternalProviderKeys.MusicBrainz, resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            _stats.RecordExternal(ExternalProviderKeys.MusicBrainz, false, sw.ElapsedMilliseconds);
            return false;
        }
    }

    /// <summary>
    /// Find the best single match (for automatic poster matching).
    /// Returns null if nothing found or no cover art available.
    /// </summary>
    public async Task<MusicResult?> SearchReleaseAsync(string title, string? artist, int? year, CancellationToken ct)
    {
        var safeTitle = (title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(safeTitle))
            return null;

        var candidates = await SearchReleasesAsync(safeTitle, artist, 5, ct);
        if (candidates.Count == 0)
            return null;

        // Score candidates and pick best
        MusicResult? best = null;
        int bestScore = -1;

        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c.CoverUrl))
                continue;

            var score = ScoreCandidate(c, safeTitle, artist, year);
            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }

        return best;
    }

    /// <summary>
    /// Returns all results (for manual poster search modal).
    /// Each result may or may not have a cover URL.
    /// </summary>
    public async Task<List<MusicResult>> SearchReleaseListAsync(string title, string? artist, CancellationToken ct)
    {
        var safeTitle = (title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(safeTitle))
            return [];

        return await SearchReleasesAsync(safeTitle, artist, 10, ct);
    }

    /// <summary>
    /// Download image bytes from a URL (Cover Art Archive or any URL).
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

    private async Task<List<MusicResult>> SearchReleasesAsync(string title, string? artist, int limit, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var url = BuildSearchUrl(title, artist, limit);
        var (clientId, _) = ResolveCreds();

        MbReleaseSearchResponse? response;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // Pass client_id as X-Application header when provided — signals a registered app to MusicBrainz
            if (!string.IsNullOrWhiteSpace(clientId))
                req.Headers.TryAddWithoutValidation("X-Application", clientId);

            using var resp = await _http.SendAsync(req, ct);
            _stats.RecordExternal(ExternalProviderKeys.MusicBrainz, resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);

            if (!resp.IsSuccessStatusCode)
                return [];

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            response = await JsonSerializer.DeserializeAsync<MbReleaseSearchResponse>(stream, JsonOpts, ct);
        }
        catch
        {
            _stats.RecordExternal(ExternalProviderKeys.MusicBrainz, false, sw.ElapsedMilliseconds);
            return [];
        }

        if (response?.Releases is null || response.Releases.Count == 0)
            return [];

        var results = new List<MusicResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var release in response.Releases)
        {
            var mbid = release.Id;
            if (string.IsNullOrWhiteSpace(mbid) || !seen.Add(mbid))
                continue;

            var releaseTitle = release.Title ?? "";
            var artistName = release.ArtistCredit?.FirstOrDefault()?.Artist?.Name
                          ?? release.ArtistCredit?.FirstOrDefault()?.Name;
            var releaseDate = release.Date ?? release.FirstReleaseDate;

            // Fetch cover from CAA (may be null if no artwork exists)
            var coverUrl = await FetchCoverUrlAsync(mbid, ct);

            results.Add(new MusicResult(mbid, releaseTitle, artistName, releaseDate, coverUrl));
        }

        return results;
    }

    private async Task<string?> FetchCoverUrlAsync(string mbid, CancellationToken ct)
    {
        // Cover Art Archive endpoint
        var url = $"{CaaBaseUrl}release/{mbid}/";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var caa = await JsonSerializer.DeserializeAsync<CaaResponse>(stream, JsonOpts, ct);

            if (caa?.Images is null)
                return null;

            // Prefer front image
            var front = caa.Images.FirstOrDefault(i => i.Front == true)
                     ?? caa.Images.FirstOrDefault();

            if (front is null)
                return null;

            // Pick best thumbnail size
            return front.Thumbnails?.Size500
                ?? front.Thumbnails?.Large
                ?? front.Image;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildSearchUrl(string title, string? artist, int limit = 5)
    {
        // Lucene query syntax for MusicBrainz
        var query = $"release:\"{EscapeLucene(title)}\"";
        if (!string.IsNullOrWhiteSpace(artist))
            query += $" AND artist:\"{EscapeLucene(artist)}\"";

        return $"{MbBaseUrl}release/?query={Uri.EscapeDataString(query)}&fmt=json&limit={limit}";
    }

    private static string EscapeLucene(string input)
    {
        // Escape Lucene special characters inside quoted strings (just the double quote)
        return input.Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static int ScoreCandidate(MusicResult c, string queryTitle, string? queryArtist, int? queryYear)
    {
        var score = 0;
        var cTitle = (c.Title ?? "").Trim().ToLowerInvariant();
        var qTitle = queryTitle.ToLowerInvariant();

        if (string.Equals(cTitle, qTitle, StringComparison.OrdinalIgnoreCase))
            score += 5;
        else if (cTitle.Contains(qTitle, StringComparison.OrdinalIgnoreCase)
              || qTitle.Contains(cTitle, StringComparison.OrdinalIgnoreCase))
            score += 2;

        if (!string.IsNullOrWhiteSpace(queryArtist) && !string.IsNullOrWhiteSpace(c.Artist))
        {
            var cArtist = c.Artist.Trim().ToLowerInvariant();
            var qArtist = queryArtist.Trim().ToLowerInvariant();
            if (string.Equals(cArtist, qArtist, StringComparison.OrdinalIgnoreCase))
                score += 4;
            else if (cArtist.Contains(qArtist, StringComparison.OrdinalIgnoreCase)
                  || qArtist.Contains(cArtist, StringComparison.OrdinalIgnoreCase))
                score += 2;
        }

        if (queryYear.HasValue && !string.IsNullOrWhiteSpace(c.Released)
            && c.Released.Length >= 4
            && int.TryParse(c.Released[..4], out var releaseYear)
            && releaseYear == queryYear.Value)
        {
            score += 2;
        }

        return score;
    }

    private static void EnsureUserAgent(HttpClient http)
    {
        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    // ─── JSON models ──────────────────────────────────────────────────────────

    private sealed class MbReleaseSearchResponse
    {
        [JsonPropertyName("releases")]
        public List<MbRelease>? Releases { get; set; }
    }

    private sealed class MbRelease
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("date")]
        public string? Date { get; set; }

        [JsonPropertyName("first-release-date")]
        public string? FirstReleaseDate { get; set; }

        [JsonPropertyName("artist-credit")]
        public List<MbArtistCredit>? ArtistCredit { get; set; }
    }

    private sealed class MbArtistCredit
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("artist")]
        public MbArtist? Artist { get; set; }
    }

    private sealed class MbArtist
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class CaaResponse
    {
        [JsonPropertyName("images")]
        public List<CaaImage>? Images { get; set; }
    }

    private sealed class CaaImage
    {
        [JsonPropertyName("front")]
        public bool? Front { get; set; }

        [JsonPropertyName("image")]
        public string? Image { get; set; }

        [JsonPropertyName("thumbnails")]
        public CaaThumbnails? Thumbnails { get; set; }
    }

    private sealed class CaaThumbnails
    {
        [JsonPropertyName("500")]
        public string? Size500 { get; set; }

        [JsonPropertyName("large")]
        public string? Large { get; set; }

        [JsonPropertyName("250")]
        public string? Size250 { get; set; }
    }
}
