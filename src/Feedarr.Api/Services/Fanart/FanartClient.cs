using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Services;

namespace Feedarr.Api.Services.Fanart;

public sealed class FanartClient
{
    private readonly HttpClient _http;
    private readonly SettingsRepository _settings;
    private readonly ProviderStatsService _stats;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public FanartClient(HttpClient http, SettingsRepository settings, ProviderStatsService stats)
    {
        _http = http;
        _settings = settings;
        _stats = stats;

        _http.BaseAddress = new Uri("https://webservice.fanart.tv/v3/");
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    private string? GetApiKey()
    {
        var ext = _settings.GetExternal(new ExternalSettings());
        if (ext.FanartEnabled == false) return null;
        var key = (ext.FanartApiKey ?? "").Trim();
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    private async Task<T?> GetJsonAsync<T>(string relativeUrl, CancellationToken ct)
    {
        var recorded = false;
        var sw = Stopwatch.StartNew();

        try
        {
            using var resp = await _http.GetAsync(relativeUrl, ct);
            var ok = resp.IsSuccessStatusCode;
            _stats.RecordFanart(ok, sw.ElapsedMilliseconds);
            recorded = true;
            if (!ok) return default;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOpts, ct);
        }
        catch
        {
            if (!recorded)
                _stats.RecordFanart(false, sw.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<bool> TestApiKeyAsync(CancellationToken ct)
    {
        var key = GetApiKey();
        if (string.IsNullOrWhiteSpace(key)) return false;

        var url = $"movies/550?api_key={Uri.EscapeDataString(key)}";
        var recorded = false;
        var sw = Stopwatch.StartNew();

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            var ok = resp.IsSuccessStatusCode;
            _stats.RecordFanart(ok, sw.ElapsedMilliseconds);
            recorded = true;
            return ok;
        }
        catch
        {
            if (!recorded)
                _stats.RecordFanart(false, sw.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<string?> GetMoviePosterUrlAsync(int tmdbId, CancellationToken ct, string? originalLanguage = null)
    {
        var key = GetApiKey();
        if (string.IsNullOrWhiteSpace(key) || tmdbId <= 0) return null;

        var url = $"movies/{tmdbId}?api_key={Uri.EscapeDataString(key)}";
        var data = await GetJsonAsync<MovieResponse>(url, ct);
        var posters = data?.MoviePoster;
        return PickPosterUrl(posters, originalLanguage);
    }

    public async Task<string?> GetMovieBannerUrlAsync(int tmdbId, CancellationToken ct)
    {
        var key = GetApiKey();
        if (string.IsNullOrWhiteSpace(key) || tmdbId <= 0) return null;

        var url = $"movies/{tmdbId}?api_key={Uri.EscapeDataString(key)}";
        var data = await GetJsonAsync<MovieResponse>(url, ct);
        var banners = data?.MovieBanner;
        return PickPosterUrl(banners);
    }

    public async Task<string?> GetTvPosterUrlAsync(int tvdbId, CancellationToken ct, string? originalLanguage = null)
    {
        var key = GetApiKey();
        if (string.IsNullOrWhiteSpace(key) || tvdbId <= 0) return null;

        var url = $"tv/{tvdbId}?api_key={Uri.EscapeDataString(key)}";
        var data = await GetJsonAsync<TvResponse>(url, ct);
        var posters = data?.TvPoster;
        return PickPosterUrl(posters, originalLanguage);
    }

    public async Task<string?> GetTvBannerUrlAsync(int tvdbId, CancellationToken ct)
    {
        var key = GetApiKey();
        if (string.IsNullOrWhiteSpace(key) || tvdbId <= 0) return null;

        var url = $"tv/{tvdbId}?api_key={Uri.EscapeDataString(key)}";
        var data = await GetJsonAsync<TvResponse>(url, ct);
        var banners = data?.TvBanner;
        return PickPosterUrl(banners);
    }

    public async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var u = url.Trim();
        if (u.StartsWith("//")) u = "https:" + u;
        var recorded = false;
        var sw = Stopwatch.StartNew();

        try
        {
            using var resp = await _http.GetAsync(u, ct);
            var ok = resp.IsSuccessStatusCode;
            _stats.RecordFanart(ok, sw.ElapsedMilliseconds);
            recorded = true;
            if (!ok) resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch
        {
            if (!recorded)
                _stats.RecordFanart(false, sw.ElapsedMilliseconds);
            throw;
        }
    }

    /// <summary>
    /// Sélectionne le meilleur poster selon la priorité:
    /// FR > EN > ES > IT > langue originale > plus de likes.
    /// </summary>
    private static string? PickPosterUrl(List<FanartPoster>? posters, string? originalLanguage = null)
    {
        if (posters is null || posters.Count == 0) return null;

        // Trier par nombre de likes (décroissant) pour chaque langue
        var sorted = posters
            .Where(x => !string.IsNullOrWhiteSpace(x.Url))
            .OrderByDescending(x => int.TryParse(x.Likes, out var likes) ? likes : 0)
            .ToList();

        // 1. Priorité: français
        var fr = sorted.FirstOrDefault(x => string.Equals(x.Lang, "fr", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(fr?.Url)) return fr.Url;

        // 2. Anglais
        var en = sorted.FirstOrDefault(x => string.Equals(x.Lang, "en", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(en?.Url)) return en.Url;

        // 3. Espagnol
        var es = sorted.FirstOrDefault(x => string.Equals(x.Lang, "es", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(es?.Url)) return es.Url;

        // 4. Italien
        var it = sorted.FirstOrDefault(x => string.Equals(x.Lang, "it", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(it?.Url)) return it.Url;

        // 5. Langue originale du contenu (si fournie)
        if (!string.IsNullOrWhiteSpace(originalLanguage))
        {
            var orig = sorted.FirstOrDefault(x => string.Equals(x.Lang, originalLanguage, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(orig?.Url)) return orig.Url;
        }

        // 6. N'importe quel poster avec le plus de likes
        return sorted.FirstOrDefault()?.Url;
    }

    private sealed class MovieResponse
    {
        [JsonPropertyName("movieposter")]
        public List<FanartPoster>? MoviePoster { get; set; }

        [JsonPropertyName("moviebanner")]
        public List<FanartPoster>? MovieBanner { get; set; }
    }

    private sealed class TvResponse
    {
        [JsonPropertyName("tvposter")]
        public List<FanartPoster>? TvPoster { get; set; }

        [JsonPropertyName("tvbanner")]
        public List<FanartPoster>? TvBanner { get; set; }
    }

    private sealed class FanartPoster
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("lang")]
        public string? Lang { get; set; }

        [JsonPropertyName("likes")]
        public string? Likes { get; set; }
    }
}
