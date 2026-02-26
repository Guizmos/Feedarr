using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Feedarr.Api.Services.ExternalProviders;

namespace Feedarr.Api.Services.Rawg;

public sealed class RawgClient
{
    private readonly HttpClient _http;
    private readonly ProviderStatsService _stats;
    private readonly ActiveExternalProviderConfigResolver _activeConfigResolver;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public RawgClient(
        HttpClient http,
        ProviderStatsService stats,
        ActiveExternalProviderConfigResolver activeConfigResolver)
    {
        _http = http;
        _stats = stats;
        _activeConfigResolver = activeConfigResolver;
    }

    // ── Internal models ──────────────────────────────────────────────────────

    private sealed class RawgSearchResponse
    {
        public List<RawgGameItem>? Results { get; set; }
    }

    private sealed class RawgGameItem
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        [JsonPropertyName("background_image")]
        public string? BackgroundImage { get; set; }
        public string? Released { get; set; }   // "YYYY-MM-DD" or null
    }

    // ── Public methods ────────────────────────────────────────────────────────

    public async Task<bool> TestApiAsync(CancellationToken ct)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) return false;

        var url = $"https://api.rawg.io/api/games?key={Uri.EscapeDataString(apiKey)}&page_size=1&search=halo";
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            _stats.RecordExternal(ExternalProviderKeys.Rawg, resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            _stats.RecordExternal(ExternalProviderKeys.Rawg, false, sw.ElapsedMilliseconds);
            return false;
        }
    }

    public async Task<(int rawgId, string title, int? year, string coverUrl)?> SearchGameAsync(
        string title, int? year, CancellationToken ct)
    {
        var results = await SearchRawAsync(title, 5, ct);
        if (results is null || results.Count == 0) return null;

        // If year provided, prefer an exact year match
        if (year.HasValue)
        {
            var yearMatch = results.FirstOrDefault(r => ParseYear(r.Released) == year.Value);
            if (yearMatch is not null && !string.IsNullOrWhiteSpace(yearMatch.BackgroundImage))
                return ToTuple(yearMatch);
        }

        var best = results.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.BackgroundImage));
        return best is null ? null : ToTuple(best);
    }

    public async Task<List<(int rawgId, string title, int? year, string coverUrl)>> SearchGameListAsync(
        string title, CancellationToken ct)
    {
        var results = await SearchRawAsync(title, 15, ct);
        if (results is null) return new List<(int, string, int?, string)>();

        return results
            .Where(r => !string.IsNullOrWhiteSpace(r.BackgroundImage))
            .Select(ToTuple)
            .ToList();
    }

    public async Task<byte[]?> DownloadCoverAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            return await _http.GetByteArrayAsync(url, ct);
        }
        catch
        {
            return null;
        }
    }

    public bool IsConfigured() => !string.IsNullOrWhiteSpace(ResolveApiKey());

    // ── Private helpers ───────────────────────────────────────────────────────

    private string? ResolveApiKey()
    {
        var config = _activeConfigResolver.Resolve(ExternalProviderKeys.Rawg);
        if (!config.Enabled) return null;
        config.Auth.TryGetValue("apiKey", out var key);
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    private async Task<List<RawgGameItem>?> SearchRawAsync(string title, int pageSize, CancellationToken ct)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        var url = $"https://api.rawg.io/api/games?key={Uri.EscapeDataString(apiKey)}&search={Uri.EscapeDataString(title)}&page_size={pageSize}";
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            sw.Stop();
            _stats.RecordExternal(ExternalProviderKeys.Rawg, resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);

            if (!resp.IsSuccessStatusCode) return null;

            var content = await resp.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<RawgSearchResponse>(content, JsonOpts);
            return parsed?.Results;
        }
        catch
        {
            sw.Stop();
            _stats.RecordExternal(ExternalProviderKeys.Rawg, false, sw.ElapsedMilliseconds);
            return null;
        }
    }

    private static (int rawgId, string title, int? year, string coverUrl) ToTuple(RawgGameItem r)
        => (r.Id, r.Name ?? "", ParseYear(r.Released), r.BackgroundImage ?? "");

    private static int? ParseYear(string? released)
    {
        if (string.IsNullOrWhiteSpace(released)) return null;
        if (released.Length >= 4 && int.TryParse(released.AsSpan(0, 4), out var y))
            return y;
        return null;
    }
}
