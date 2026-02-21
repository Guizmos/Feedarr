using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
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

        var safeTitle = (title ?? "").Trim();
        var safeArtist = (artist ?? "").Trim();
        if (string.IsNullOrWhiteSpace(safeTitle) && string.IsNullOrWhiteSpace(safeArtist))
            return null;

        if (!string.IsNullOrWhiteSpace(safeTitle))
        {
            var trackEndpoint = BuildTrackSearchEndpoint(apiKey, safeTitle, safeArtist);
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

        var albumEndpoint = BuildAlbumSearchEndpoint(apiKey, safeTitle, safeArtist);
        using var albumResp = await GetTrackedAsync(albumEndpoint, ct);
        if (!albumResp.IsSuccessStatusCode)
            return null;

        var albumPayload = await DeserializePayloadAsync<AlbumSearchResponse>(albumResp, ct);
        var bestAlbum = albumPayload?.Album?
            .Where(a => !string.IsNullOrWhiteSpace(a.IdAlbum))
            .OrderByDescending(a => MatchScore(safeTitle, safeArtist, year, a.StrAlbum, a.StrArtist, a.IntYearReleased))
            .FirstOrDefault();
        return bestAlbum is null ? null : MapAlbum(bestAlbum);
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

    private string BuildTrackSearchEndpoint(string apiKey, string title, string artist)
    {
        var query = string.IsNullOrWhiteSpace(artist)
            ? $"searchtrack.php?t={Uri.EscapeDataString(title)}"
            : $"searchtrack.php?s={Uri.EscapeDataString(artist)}&t={Uri.EscapeDataString(title)}";
        return BuildEndpoint(apiKey, query);
    }

    private string BuildAlbumSearchEndpoint(string apiKey, string title, string artist)
    {
        var query = string.IsNullOrWhiteSpace(artist)
            ? $"searchalbum.php?a={Uri.EscapeDataString(title)}"
            : $"searchalbum.php?s={Uri.EscapeDataString(artist)}&a={Uri.EscapeDataString(title)}";
        return BuildEndpoint(apiKey, query);
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
        var payload = await response.Content.ReadAsStringAsync(ct);
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
