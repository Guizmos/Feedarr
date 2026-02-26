using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services;

namespace Feedarr.Api.Services.Fanart;

public sealed class FanartClient
{
    private readonly HttpClient _http;
    private readonly ProviderStatsService _stats;
    private readonly ActiveExternalProviderConfigResolver _activeConfigResolver;
    private readonly SettingsRepository _settings;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public FanartClient(
        HttpClient http,
        ProviderStatsService stats,
        ActiveExternalProviderConfigResolver activeConfigResolver,
        SettingsRepository settings)
    {
        _http = http;
        _stats = stats;
        _activeConfigResolver = activeConfigResolver;
        _settings = settings;

        _http.BaseAddress = new Uri("https://webservice.fanart.tv/v3/");
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    private string? GetApiKey()
    {
        var active = _activeConfigResolver.Resolve(ExternalProviderKeys.Fanart);
        if (!active.Enabled) return null;
        if (!active.Auth.TryGetValue("apiKey", out var activeValue))
            return null;

        var activeKey = (activeValue ?? "").Trim();
        return string.IsNullOrWhiteSpace(activeKey) ? null : activeKey;
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

    private IReadOnlyList<string> GetPosterLanguagePriority()
    {
        var ui = _settings.GetUi(new UiSettings());
        return UiLanguageCatalog.BuildPosterLanguagePriority(ui.MediaInfoLanguage);
    }

    public async Task<string?> GetMoviePosterUrlAsync(int tmdbId, CancellationToken ct, string? originalLanguage = null)
    {
        var key = GetApiKey();
        if (string.IsNullOrWhiteSpace(key) || tmdbId <= 0) return null;

        var url = $"movies/{tmdbId}?api_key={Uri.EscapeDataString(key)}";
        var data = await GetJsonAsync<MovieResponse>(url, ct);
        var posters = data?.MoviePoster;
        return PickPosterUrl(posters, GetPosterLanguagePriority(), originalLanguage);
    }

    public async Task<string?> GetMovieBannerUrlAsync(int tmdbId, CancellationToken ct)
    {
        var key = GetApiKey();
        if (string.IsNullOrWhiteSpace(key) || tmdbId <= 0) return null;

        var url = $"movies/{tmdbId}?api_key={Uri.EscapeDataString(key)}";
        var data = await GetJsonAsync<MovieResponse>(url, ct);
        var banners = data?.MovieBanner;
        return PickPosterUrl(banners, GetPosterLanguagePriority());
    }

    public async Task<string?> GetTvPosterUrlAsync(int tvdbId, CancellationToken ct, string? originalLanguage = null)
    {
        var key = GetApiKey();
        if (string.IsNullOrWhiteSpace(key) || tvdbId <= 0) return null;

        var url = $"tv/{tvdbId}?api_key={Uri.EscapeDataString(key)}";
        var data = await GetJsonAsync<TvResponse>(url, ct);
        var posters = data?.TvPoster;
        return PickPosterUrl(posters, GetPosterLanguagePriority(), originalLanguage);
    }

    public async Task<string?> GetTvBannerUrlAsync(int tvdbId, CancellationToken ct)
    {
        var key = GetApiKey();
        if (string.IsNullOrWhiteSpace(key) || tvdbId <= 0) return null;

        var url = $"tv/{tvdbId}?api_key={Uri.EscapeDataString(key)}";
        var data = await GetJsonAsync<TvResponse>(url, ct);
        var banners = data?.TvBanner;
        return PickPosterUrl(banners, GetPosterLanguagePriority());
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
    /// Sélectionne le meilleur poster selon la priorité de langue utilisateur
    /// (depuis UiLanguageCatalog.BuildPosterLanguagePriority), puis langue originale, puis plus de likes.
    /// </summary>
    private static string? PickPosterUrl(List<FanartPoster>? posters, IReadOnlyList<string> languagePriority, string? originalLanguage = null)
    {
        if (posters is null || posters.Count == 0) return null;

        // Trier par nombre de likes (décroissant) pour départager à rang égal
        var sorted = posters
            .Where(x => !string.IsNullOrWhiteSpace(x.Url))
            .OrderByDescending(x => int.TryParse(x.Likes, out var likes) ? likes : 0)
            .ToList();

        // 1. Priorité utilisateur (ex: fr > en > null, ou en > fr > null)
        foreach (var lang in languagePriority)
        {
            if (string.Equals(lang, "null", StringComparison.OrdinalIgnoreCase))
                break; // "null" = langue neutre, on passe aux fallbacks

            var match = sorted.FirstOrDefault(x => string.Equals(x.Lang, lang, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match?.Url)) return match.Url;
        }

        // 2. Langue originale du contenu (si fournie et non déjà couverte)
        if (!string.IsNullOrWhiteSpace(originalLanguage))
        {
            var orig = sorted.FirstOrDefault(x => string.Equals(x.Lang, originalLanguage, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(orig?.Url)) return orig.Url;
        }

        // 3. N'importe quel poster avec le plus de likes
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
