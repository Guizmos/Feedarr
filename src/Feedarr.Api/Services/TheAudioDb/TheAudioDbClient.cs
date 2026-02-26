using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services;

namespace Feedarr.Api.Services.TheAudioDb;

public sealed class TheAudioDbClient
{
    public sealed record AudioResult(
        string ProviderId,
        string Title,
        string? Artist,
        string? Description,
        string? Genre,
        string? Released,
        string? PosterUrl,
        double? Rating);

    private readonly HttpClient _http;
    private readonly ActiveExternalProviderConfigResolver _resolver;
    private readonly ProviderStatsService _stats;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TheAudioDbClient(
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
        var apiKey = ResolveCreds();
        if (string.IsNullOrWhiteSpace(apiKey))
            return false;

        var endpoint = BuildEndpoint(apiKey, "searchalbum.php?s=Daft%20Punk&a=Random%20Access%20Memories");
        using var resp = await GetTrackedAsync(endpoint, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<AudioResult?> SearchAudioAsync(string title, string? artist, int? year, CancellationToken ct)
    {
        var apiKey = ResolveCreds();
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        var safeTitle = CleanTitle((title ?? "").Trim());
        var safeArtist = (artist ?? "").Trim();
        if (string.IsNullOrWhiteSpace(safeTitle) && string.IsNullOrWhiteSpace(safeArtist))
            return null;

        // When artist is known: track search first (requires both s= and t= — free API)
        if (!string.IsNullOrWhiteSpace(safeArtist) && !string.IsNullOrWhiteSpace(safeTitle))
        {
            var trackEndpoint = BuildEndpoint(apiKey, $"searchtrack.php?s={Uri.EscapeDataString(safeArtist)}&t={Uri.EscapeDataString(safeTitle)}");
            using var trackResp = await GetTrackedAsync(trackEndpoint, ct);
            if (trackResp.IsSuccessStatusCode)
            {
                var trackPayload = await DeserializePayloadAsync<TrackSearchResponse>(trackResp, ct);
                var bestTrack = trackPayload?.Track?
                    .Where(t => !string.IsNullOrWhiteSpace(t.IdTrack))
                    .OrderByDescending(t => MatchScore(safeTitle, safeArtist, year, t.StrTrack, t.StrArtist, t.IntYearReleased))
                    .FirstOrDefault();
                if (bestTrack is not null)
                    return MapTrack(bestTrack);
            }
        }

        // When artist is known: album search (requires both s= and a= — free API)
        if (!string.IsNullOrWhiteSpace(safeArtist))
        {
            var albumEndpoint = BuildEndpoint(apiKey, $"searchalbum.php?s={Uri.EscapeDataString(safeArtist)}&a={Uri.EscapeDataString(safeTitle)}");
            using var albumResp = await GetTrackedAsync(albumEndpoint, ct);
            if (albumResp.IsSuccessStatusCode)
            {
                var albumPayload = await DeserializePayloadAsync<AlbumSearchResponse>(albumResp, ct);
                var best = albumPayload?.Album?
                    .Where(a => !string.IsNullOrWhiteSpace(a.IdAlbum))
                    .OrderByDescending(a => MatchScore(safeTitle, safeArtist, year, a.StrAlbum, a.StrArtist, a.IntYearReleased))
                    .FirstOrDefault();
                if (best is not null)
                    return MapAlbum(best);
            }
        }

        // No artist: try progressive split (N first words = artist, rest = album).
        // Handles scene-style titles like "ACDC Back In Black" or "The Cure Greatest Hits".
        // Note: searchtrack/searchalbum without s= are premium-only and intentionally NOT called here.
        if (string.IsNullOrWhiteSpace(safeArtist) && !string.IsNullOrWhiteSpace(safeTitle))
        {
            var words = safeTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            AudioAlbumItem? splitBest = null;
            int splitBestScore = -1;

            for (int n = 1; n <= Math.Min(4, words.Length - 1); n++)
            {
                var tryArtist = string.Join(" ", words[..n]);
                var tryAlbum = string.Join(" ", words[n..]);
                if (string.IsNullOrWhiteSpace(tryAlbum)) continue;

                var endpoint = BuildEndpoint(apiKey, $"searchalbum.php?s={Uri.EscapeDataString(tryArtist)}&a={Uri.EscapeDataString(tryAlbum)}");
                using var resp = await GetTrackedAsync(endpoint, ct);
                if (!resp.IsSuccessStatusCode) continue;

                var payload = await DeserializePayloadAsync<AlbumSearchResponse>(resp, ct);
                if (payload?.Album is null) continue;

                var candidate = payload.Album
                    .Where(a => !string.IsNullOrWhiteSpace(a.IdAlbum))
                    .OrderByDescending(a => MatchScore(tryAlbum, tryArtist, year, a.StrAlbum, a.StrArtist, a.IntYearReleased))
                    .FirstOrDefault();
                if (candidate is null) continue;

                var score = MatchScore(tryAlbum, tryArtist, year, candidate.StrAlbum, candidate.StrArtist, candidate.IntYearReleased);
                if (score > splitBestScore)
                {
                    splitBestScore = score;
                    splitBest = candidate;
                }
            }

            if (splitBest is not null)
                return MapAlbum(splitBest);

            // Final fallback: treat entire query as artist name (e.g., typing "Genesis" to find all albums)
            var artistEndpoint = BuildEndpoint(apiKey, $"searchalbum.php?s={Uri.EscapeDataString(safeTitle)}");
            using var artistResp = await GetTrackedAsync(artistEndpoint, ct);
            if (artistResp.IsSuccessStatusCode)
            {
                var artistPayload = await DeserializePayloadAsync<AlbumSearchResponse>(artistResp, ct);
                return artistPayload?.Album?
                    .Where(a => !string.IsNullOrWhiteSpace(a.IdAlbum))
                    .OrderByDescending(a => !string.IsNullOrWhiteSpace(a.StrAlbumThumb) ? 1 : 0)
                    .Select(MapAlbum)
                    .FirstOrDefault();
            }
        }

        return null;
    }

    // Returns all matching audio results (tracks + albums) for manual poster search
    public async Task<List<AudioResult>> SearchAudioListAsync(string title, string? artist, int? year, CancellationToken ct)
    {
        var apiKey = ResolveCreds();
        if (string.IsNullOrWhiteSpace(apiKey))
            return [];

        var safeTitle = CleanTitle((title ?? "").Trim());
        var safeArtist = (artist ?? "").Trim();
        if (string.IsNullOrWhiteSpace(safeTitle) && string.IsNullOrWhiteSpace(safeArtist))
            return [];

        var results = new List<AudioResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Artist known: track search (s= + t= — free API)
        if (!string.IsNullOrWhiteSpace(safeArtist) && !string.IsNullOrWhiteSpace(safeTitle))
        {
            var trackEndpoint = BuildEndpoint(apiKey, $"searchtrack.php?s={Uri.EscapeDataString(safeArtist)}&t={Uri.EscapeDataString(safeTitle)}");
            using var trackResp = await GetTrackedAsync(trackEndpoint, ct);
            if (trackResp.IsSuccessStatusCode)
            {
                var payload = await DeserializePayloadAsync<TrackSearchResponse>(trackResp, ct);
                foreach (var t in (payload?.Track ?? [])
                    .Where(t => !string.IsNullOrWhiteSpace(t.IdTrack))
                    .OrderByDescending(t => MatchScore(safeTitle, safeArtist, year, t.StrTrack, t.StrArtist, t.IntYearReleased)))
                {
                    var mapped = MapTrack(t);
                    if (!string.IsNullOrWhiteSpace(mapped.PosterUrl) && seen.Add(mapped.ProviderId))
                        results.Add(mapped);
                }
            }
        }

        // Artist known: album search (s= + a= — free API)
        if (!string.IsNullOrWhiteSpace(safeArtist))
        {
            var albumEndpoint = BuildEndpoint(apiKey, $"searchalbum.php?s={Uri.EscapeDataString(safeArtist)}&a={Uri.EscapeDataString(safeTitle)}");
            using var albumResp = await GetTrackedAsync(albumEndpoint, ct);
            if (albumResp.IsSuccessStatusCode)
            {
                var payload = await DeserializePayloadAsync<AlbumSearchResponse>(albumResp, ct);
                foreach (var a in (payload?.Album ?? [])
                    .Where(a => !string.IsNullOrWhiteSpace(a.IdAlbum))
                    .OrderByDescending(a => MatchScore(safeTitle, safeArtist, year, a.StrAlbum, a.StrArtist, a.IntYearReleased)))
                {
                    var mapped = MapAlbum(a);
                    if (seen.Add(mapped.ProviderId))
                        results.Add(mapped);
                }
            }
        }

        // No artist: progressive split (N first words = artist, rest = album)
        // + full-title artist search (e.g., "Genesis" → all Genesis albums)
        if (string.IsNullOrWhiteSpace(safeArtist) && !string.IsNullOrWhiteSpace(safeTitle))
        {
            var words = safeTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int n = 1; n <= Math.Min(4, words.Length - 1); n++)
            {
                var tryArtist = string.Join(" ", words[..n]);
                var tryAlbum = string.Join(" ", words[n..]);
                if (string.IsNullOrWhiteSpace(tryAlbum)) continue;

                var endpoint = BuildEndpoint(apiKey, $"searchalbum.php?s={Uri.EscapeDataString(tryArtist)}&a={Uri.EscapeDataString(tryAlbum)}");
                using var resp = await GetTrackedAsync(endpoint, ct);
                if (!resp.IsSuccessStatusCode) continue;

                var payload = await DeserializePayloadAsync<AlbumSearchResponse>(resp, ct);
                foreach (var a in (payload?.Album ?? [])
                    .Where(a => !string.IsNullOrWhiteSpace(a.IdAlbum))
                    .OrderByDescending(a => MatchScore(tryAlbum, tryArtist, year, a.StrAlbum, a.StrArtist, a.IntYearReleased)))
                {
                    var mapped = MapAlbum(a);
                    if (seen.Add(mapped.ProviderId))
                        results.Add(mapped);
                }
            }

            // Full title as artist name (returns the whole discography — useful for manual search)
            var artistEndpoint = BuildEndpoint(apiKey, $"searchalbum.php?s={Uri.EscapeDataString(safeTitle)}");
            using var artistResp = await GetTrackedAsync(artistEndpoint, ct);
            if (artistResp.IsSuccessStatusCode)
            {
                var artistPayload = await DeserializePayloadAsync<AlbumSearchResponse>(artistResp, ct);
                foreach (var a in (artistPayload?.Album ?? [])
                    .Where(a => !string.IsNullOrWhiteSpace(a.IdAlbum))
                    .OrderByDescending(a => !string.IsNullOrWhiteSpace(a.StrAlbumThumb) ? 1 : 0))
                {
                    var mapped = MapAlbum(a);
                    if (seen.Add(mapped.ProviderId))
                        results.Add(mapped);
                }
            }
        }

        return results;
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
        var active = _resolver.Resolve(ExternalProviderKeys.TheAudioDb);
        if (!active.Enabled)
            return null;

        if (!active.Auth.TryGetValue("apiKey", out var apiKey))
            return null;

        var key = (apiKey ?? "").Trim();
        if (string.IsNullOrWhiteSpace(key))
            return null;

        return key;
    }


    private string BuildEndpoint(string apiKey, string relativePath)
    {
        var active = _resolver.Resolve(ExternalProviderKeys.TheAudioDb);
        var rawBase = string.IsNullOrWhiteSpace(active.BaseUrl)
            ? "https://www.theaudiodb.com/api/v1/json/"
            : active.BaseUrl!;
        if (!Uri.TryCreate(rawBase, UriKind.Absolute, out var baseUri))
            baseUri = new Uri("https://www.theaudiodb.com/api/v1/json/");

        var normalized = baseUri.ToString();
        if (!normalized.EndsWith("/", StringComparison.Ordinal))
            normalized += "/";

        var fullPath = $"{apiKey.Trim().Trim('/')}/{relativePath.TrimStart('/')}";
        return new Uri(new Uri(normalized, UriKind.Absolute), fullPath).ToString();
    }

    /// <summary>
    /// Strips scene-style noise from audio release names before querying TheAudioDB.
    /// Example: "ACDC.Black.Ice.2008.FLAC[16bit.44.1kHz]-RmKv" → "ACDC Black Ice"
    /// </summary>
    private static string CleanTitle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input ?? "";

        var s = input.Trim();
        var wasSceneName = false;

        // 1. Dot-to-space for scene-style names (≥3 dots AND ≤1 space)
        if (s.Count(c => c == '.') >= 3 && s.Count(c => c == ' ') <= 1)
        {
            s = s.Replace('.', ' ').Trim();
            wasSceneName = true;
        }

        // 2. Strip audio format/quality suffix
        s = StripFormatSuffix(s);

        // 3. Strip trailing year
        s = Regex.Replace(s, @"\s*[\(\[]?\s*\b(19|20)\d{2}\b\s*[\)\]]?\s*$", "").Trim();

        // 4. Strip "Remastered" suffix
        s = Regex.Replace(s, @"\s+Remaster(?:ed|ised?)?\s*\d{0,4}\s*$", "", RegexOptions.IgnoreCase).Trim();

        // 5. Strip scene release group tag (e.g. "-RmKv", "-NOTAG") — only for scene-style names
        if (wasSceneName &&
            !s.Contains(" - ", StringComparison.Ordinal) &&
            !s.Contains(" – ", StringComparison.Ordinal) &&
            !s.Contains(" — ", StringComparison.Ordinal))
        {
            s = Regex.Replace(s, @"\s*-[A-Za-z0-9]{4,12}\s*$", "").Trim();
        }

        return s;
    }

    private static string StripFormatSuffix(string s)
    {
        var markers = new[]
        {
            " FLAC", "[FLAC", "(FLAC", ".FLAC",
            " MP3",  "[MP3",  "(MP3",  ".MP3",
            " WEB-", "[WEB-", ".WEB-",
            " SACD", " AAC",  " ALAC", " OGG",
            "[16bit", "(16bit", ".16bit",
            "[24bit", "(24bit", ".24bit",
            " 320kbps", " 256kbps", " 192kbps", " 128kbps",
        };

        var earliest = -1;
        foreach (var marker in markers)
        {
            var idx = s.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx > 0 && (earliest < 0 || idx < earliest))
                earliest = idx;
        }

        return earliest > 0 ? s[..earliest].Trim() : s;
    }

    private static int MatchScore(string title, string artist, int? year, string? candidateTitle, string? candidateArtist, string? releasedYear)
    {
        var score = 0;
        var queryTitle = (title ?? "").Trim().ToLowerInvariant();
        var queryArtist = (artist ?? "").Trim().ToLowerInvariant();
        var normalizedTitle = (candidateTitle ?? "").Trim().ToLowerInvariant();
        var normalizedArtist = (candidateArtist ?? "").Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(queryTitle))
        {
            if (normalizedTitle == queryTitle) score += 5;
            else if (normalizedTitle.Contains(queryTitle, StringComparison.OrdinalIgnoreCase)) score += 3;
            else if (queryTitle.Contains(normalizedTitle, StringComparison.OrdinalIgnoreCase)) score += 2;
        }

        if (!string.IsNullOrWhiteSpace(queryArtist))
        {
            if (normalizedArtist == queryArtist) score += 3;
            else if (normalizedArtist.Contains(queryArtist, StringComparison.OrdinalIgnoreCase)) score += 2;
        }

        if (year.HasValue && int.TryParse((releasedYear ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedYear))
        {
            if (parsedYear == year.Value) score += 2;
        }

        return score;
    }

    private static AudioResult MapTrack(AudioTrackItem item)
    {
        return new AudioResult(
            item.IdTrack!.Trim(),
            (item.StrTrack ?? item.StrAlbum ?? "").Trim(),
            item.StrArtist?.Trim(),
            item.StrDescriptionEN?.Trim(),
            item.StrGenre?.Trim(),
            item.IntYearReleased?.Trim(),
            NormalizeAbsoluteUrl(item.StrTrackThumb ?? item.StrAlbumThumb),
            ParseScore(item.IntScore));
    }

    private static AudioResult MapAlbum(AudioAlbumItem item)
    {
        return new AudioResult(
            item.IdAlbum!.Trim(),
            (item.StrAlbum ?? "").Trim(),
            item.StrArtist?.Trim(),
            item.StrDescriptionEN?.Trim(),
            item.StrGenre?.Trim(),
            item.IntYearReleased?.Trim(),
            NormalizeAbsoluteUrl(item.StrAlbumThumb),
            ParseScore(item.IntScore));
    }

    private static double? ParseScore(string? raw)
    {
        if (!double.TryParse((raw ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;

        return value > 0 ? value : null;
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

    private async Task<HttpResponseMessage> GetTrackedAsync(string endpoint, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await _http.GetAsync(endpoint, ct);
            _stats.RecordExternal(ExternalProviderKeys.TheAudioDb, resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);
            return resp;
        }
        catch
        {
            _stats.RecordExternal(ExternalProviderKeys.TheAudioDb, ok: false, sw.ElapsedMilliseconds);
            throw;
        }
    }

    private static async Task<TPayload?> DeserializePayloadAsync<TPayload>(HttpResponseMessage response, CancellationToken ct)
        where TPayload : class
    {
        var raw = await response.Content.ReadAsStringAsync(ct);
        // Strip BOM (\uFEFF) that TheAudioDB occasionally returns for empty responses
        var payload = raw?.TrimStart('\uFEFF').Trim();
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        try
        {
            return JsonSerializer.Deserialize<TPayload>(payload, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class TrackSearchResponse
    {
        [JsonPropertyName("track")]
        public List<AudioTrackItem>? Track { get; set; }
    }

    private sealed class AlbumSearchResponse
    {
        [JsonPropertyName("album")]
        public List<AudioAlbumItem>? Album { get; set; }
    }

    private sealed class AudioTrackItem
    {
        [JsonPropertyName("idTrack")]
        public string? IdTrack { get; set; }

        [JsonPropertyName("strTrack")]
        public string? StrTrack { get; set; }

        [JsonPropertyName("strAlbum")]
        public string? StrAlbum { get; set; }

        [JsonPropertyName("strArtist")]
        public string? StrArtist { get; set; }

        [JsonPropertyName("strDescriptionEN")]
        public string? StrDescriptionEN { get; set; }

        [JsonPropertyName("strGenre")]
        public string? StrGenre { get; set; }

        [JsonPropertyName("intYearReleased")]
        public string? IntYearReleased { get; set; }

        [JsonPropertyName("strTrackThumb")]
        public string? StrTrackThumb { get; set; }

        [JsonPropertyName("strAlbumThumb")]
        public string? StrAlbumThumb { get; set; }

        [JsonPropertyName("intScore")]
        public string? IntScore { get; set; }
    }

    private sealed class AudioAlbumItem
    {
        [JsonPropertyName("idAlbum")]
        public string? IdAlbum { get; set; }

        [JsonPropertyName("strAlbum")]
        public string? StrAlbum { get; set; }

        [JsonPropertyName("strArtist")]
        public string? StrArtist { get; set; }

        [JsonPropertyName("strDescriptionEN")]
        public string? StrDescriptionEN { get; set; }

        [JsonPropertyName("strGenre")]
        public string? StrGenre { get; set; }

        [JsonPropertyName("intYearReleased")]
        public string? IntYearReleased { get; set; }

        [JsonPropertyName("strAlbumThumb")]
        public string? StrAlbumThumb { get; set; }

        [JsonPropertyName("intScore")]
        public string? IntScore { get; set; }
    }
}
