using System.Collections.Concurrent;
using System.Linq;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Services.Arr;

public sealed class ArrLibraryCacheService
{
    private readonly ArrApplicationRepository _repo;
    private readonly SonarrClient _sonarr;
    private readonly RadarrClient _radarr;

    // Cache: tvdbId -> (seriesId, titleSlug, baseUrl, expiresAt)
    private readonly ConcurrentDictionary<int, (int seriesId, string titleSlug, string baseUrl, DateTimeOffset expiresAt)> _sonarrCache = new();

    // Cache: tmdbId -> (movieId, baseUrl, expiresAt)
    private readonly ConcurrentDictionary<int, (int movieId, string baseUrl, DateTimeOffset expiresAt)> _radarrCache = new();

    // Title-based caches for fallback matching (normalized title -> entry)
    private readonly ConcurrentDictionary<string, (int? tvdbId, int seriesId, string titleSlug, string baseUrl, DateTimeOffset expiresAt)> _sonarrTitleCache = new();
    private readonly ConcurrentDictionary<string, (int? tmdbId, int movieId, string baseUrl, DateTimeOffset expiresAt)> _radarrTitleCache = new();

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private static string NormalizeTitle(string? title)
    {
#if false
        if (string.IsNullOrWhiteSpace(title)) return "";
        // Lowercase, remove accents, remove non-alphanumeric except spaces
        var normalized = title.ToLowerInvariant().Trim();
        // Simple accent removal
        normalized = normalized
            .Replace("é", "e").Replace("è", "e").Replace("ê", "e").Replace("ë", "e")
            .Replace("à", "a").Replace("â", "a").Replace("ä", "a")
            .Replace("ù", "u").Replace("û", "u").Replace("ü", "u")
            .Replace("ô", "o").Replace("ö", "o")
            .Replace("î", "i").Replace("ï", "i")
            .Replace("ç", "c");
        // Remove non-alphanumeric (keep spaces for now)
        var sb = new System.Text.StringBuilder();
        foreach (var c in normalized)
        {
            if (char.IsLetterOrDigit(c) || c == ' ')
                sb.Append(c);
        }
        // Collapse multiple spaces and trim
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
#endif
        return TitleNormalizer.NormalizeTitleStrict(title);
    }

    public ArrLibraryCacheService(
        ArrApplicationRepository repo,
        SonarrClient sonarr,
        RadarrClient radarr)
    {
        _repo = repo;
        _sonarr = sonarr;
        _radarr = radarr;
    }

    private void AddSonarrTitleKeys(string? title, (int? tvdbId, int seriesId, string titleSlug, string baseUrl, DateTimeOffset expiresAt) entry)
    {
        var variants = TitleNormalizer.BuildTitleVariants(title);
        if (variants.Length == 0) return;
        foreach (var key in variants)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            _sonarrTitleCache.AddOrUpdate(key, entry, (_, existing) =>
            {
                if (existing.expiresAt <= DateTimeOffset.UtcNow) return entry;
                if (!existing.tvdbId.HasValue && entry.tvdbId.HasValue) return entry;
                return existing;
            });
        }
    }

    private void AddRadarrTitleKeys(string? title, (int? tmdbId, int movieId, string baseUrl, DateTimeOffset expiresAt) entry)
    {
        var variants = TitleNormalizer.BuildTitleVariants(title);
        if (variants.Length == 0) return;
        foreach (var key in variants)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            _radarrTitleCache.AddOrUpdate(key, entry, (_, existing) =>
            {
                if (existing.expiresAt <= DateTimeOffset.UtcNow) return entry;
                if (!existing.tmdbId.HasValue && entry.tmdbId.HasValue) return entry;
                return existing;
            });
        }
    }

    private bool IsSonarrCacheStale()
    {
        var now = DateTimeOffset.UtcNow;
        var hasFreshId = _sonarrCache.Values.Any(e => e.expiresAt > now);
        var hasFreshTitle = _sonarrTitleCache.Values.Any(e => e.expiresAt > now);
        return !hasFreshId && !hasFreshTitle;
    }

    private bool IsRadarrCacheStale()
    {
        var now = DateTimeOffset.UtcNow;
        var hasFreshId = _radarrCache.Values.Any(e => e.expiresAt > now);
        var hasFreshTitle = _radarrTitleCache.Values.Any(e => e.expiresAt > now);
        return !hasFreshId && !hasFreshTitle;
    }

    public async Task RefreshSonarrCacheAsync(CancellationToken ct)
    {
        var app = _repo.GetDefault("sonarr");
        if (app is null || string.IsNullOrWhiteSpace(app.ApiKeyEncrypted)) return;

        try
        {
            var series = await _sonarr.GetAllSeriesAsync(app.BaseUrl, app.ApiKeyEncrypted, ct);
            var expiresAt = DateTimeOffset.UtcNow.Add(CacheTtl);

            // Clear title cache before refresh
            _sonarrTitleCache.Clear();

            foreach (var s in series)
            {
                if (s.TvdbId > 0)
                {
                    _sonarrCache[s.TvdbId] = (s.Id, s.TitleSlug, app.BaseUrl, expiresAt);
                }

                var entry = (s.TvdbId > 0 ? (int?)s.TvdbId : null, s.Id, s.TitleSlug, app.BaseUrl, expiresAt);

                // Add main title to cache
                AddSonarrTitleKeys(s.Title, entry);

                // Add all alternate titles to cache
                if (s.AlternateTitles is { Count: > 0 })
                {
                    foreach (var alt in s.AlternateTitles)
                    {
                        AddSonarrTitleKeys(alt.Title, entry);
                    }
                }
            }
        }
        catch
        {
            // Silently fail - cache will just be stale
        }
    }

    public async Task RefreshRadarrCacheAsync(CancellationToken ct)
    {
        var app = _repo.GetDefault("radarr");
        if (app is null || string.IsNullOrWhiteSpace(app.ApiKeyEncrypted)) return;

        try
        {
            var movies = await _radarr.GetAllMoviesAsync(app.BaseUrl, app.ApiKeyEncrypted, ct);
            var expiresAt = DateTimeOffset.UtcNow.Add(CacheTtl);

            // Clear title cache before refresh
            _radarrTitleCache.Clear();

            foreach (var m in movies)
            {
                if (m.TmdbId > 0)
                {
                    _radarrCache[m.TmdbId] = (m.Id, app.BaseUrl, expiresAt);
                }

                var entry = (m.TmdbId > 0 ? (int?)m.TmdbId : null, m.Id, app.BaseUrl, expiresAt);

                // Add main title to cache
                AddRadarrTitleKeys(m.Title, entry);

                // Add original title if different
                if (!string.IsNullOrWhiteSpace(m.OriginalTitle) && m.OriginalTitle != m.Title)
                {
                    AddRadarrTitleKeys(m.OriginalTitle, entry);
                }

                // Add all alternate titles to cache
                if (m.AlternateTitles is { Count: > 0 })
                {
                    foreach (var alt in m.AlternateTitles)
                    {
                        AddRadarrTitleKeys(alt.Title, entry);
                    }
                }
            }
        }
        catch
        {
            // Silently fail - cache will just be stale
        }
    }

    public (bool exists, int? seriesId, string? openUrl) CheckSonarrExists(int tvdbId)
    {
        if (_sonarrCache.TryGetValue(tvdbId, out var entry))
        {
            if (entry.expiresAt > DateTimeOffset.UtcNow)
            {
                var openUrl = _sonarr.BuildOpenUrl(entry.baseUrl, entry.titleSlug);
                return (true, entry.seriesId, openUrl);
            }
            _sonarrCache.TryRemove(tvdbId, out _);
        }
        return (false, null, null);
    }

    public (bool exists, int? movieId, string? openUrl) CheckRadarrExists(int tmdbId)
    {
        if (_radarrCache.TryGetValue(tmdbId, out var entry))
        {
            if (entry.expiresAt > DateTimeOffset.UtcNow)
            {
                // Use tmdbId for URL, not internal movieId
                var openUrl = _radarr.BuildOpenUrl(entry.baseUrl, tmdbId);
                return (true, entry.movieId, openUrl);
            }
            _radarrCache.TryRemove(tmdbId, out _);
        }
        return (false, null, null);
    }

    /// <summary>
    /// Fallback: check Sonarr by title when tvdbId is not available
    /// </summary>
    public (bool exists, int? seriesId, string? openUrl, int? foundTvdbId) CheckSonarrExistsByTitle(string? title)
    {
        var variants = TitleNormalizer.BuildTitleVariants(title);
        if (variants.Length == 0)
            return (false, null, null, null);

        var now = DateTimeOffset.UtcNow;
        foreach (var key in variants)
        {
            if (!_sonarrTitleCache.TryGetValue(key, out var entry)) continue;
            if (entry.expiresAt <= now)
            {
                _sonarrTitleCache.TryRemove(key, out _);
                continue;
            }

            var openUrl = string.IsNullOrWhiteSpace(entry.titleSlug)
                ? null
                : _sonarr.BuildOpenUrl(entry.baseUrl, entry.titleSlug);
            return (true, entry.seriesId, openUrl, entry.tvdbId);
        }
        return (false, null, null, null);
    }

    /// <summary>
    /// Fallback: check Radarr by title when tmdbId is not available
    /// </summary>
    public (bool exists, int? movieId, string? openUrl, int? foundTmdbId) CheckRadarrExistsByTitle(string? title)
    {
        var variants = TitleNormalizer.BuildTitleVariants(title);
        if (variants.Length == 0)
            return (false, null, null, null);

        var now = DateTimeOffset.UtcNow;
        foreach (var key in variants)
        {
            if (!_radarrTitleCache.TryGetValue(key, out var entry)) continue;
            if (entry.expiresAt <= now)
            {
                _radarrTitleCache.TryRemove(key, out _);
                continue;
            }

            var openUrl = entry.tmdbId.HasValue
                ? _radarr.BuildOpenUrl(entry.baseUrl, entry.tmdbId.Value)
                : null;
            return (true, entry.movieId, openUrl, entry.tmdbId);
        }
        return (false, null, null, null);
    }

    public async Task<(bool exists, int? seriesId, string? openUrl)> CheckSonarrExistsWithRefreshAsync(
        int tvdbId, CancellationToken ct)
    {
        var cached = CheckSonarrExists(tvdbId);
        if (cached.exists) return cached;

        // Check if cache is stale (no entries or all expired)
        if (IsSonarrCacheStale())
        {
            await RefreshSonarrCacheAsync(ct);
            return CheckSonarrExists(tvdbId);
        }

        return (false, null, null);
    }

    public async Task<(bool exists, int? movieId, string? openUrl)> CheckRadarrExistsWithRefreshAsync(
        int tmdbId, CancellationToken ct)
    {
        var cached = CheckRadarrExists(tmdbId);
        if (cached.exists) return cached;

        // Check if cache is stale
        if (IsRadarrCacheStale())
        {
            await RefreshRadarrCacheAsync(ct);
            return CheckRadarrExists(tmdbId);
        }

        return (false, null, null);
    }

    public async Task EnsureSonarrCacheFreshAsync(CancellationToken ct)
    {
        if (IsSonarrCacheStale())
        {
            await RefreshSonarrCacheAsync(ct);
        }
    }

    public async Task EnsureRadarrCacheFreshAsync(CancellationToken ct)
    {
        if (IsRadarrCacheStale())
        {
            await RefreshRadarrCacheAsync(ct);
        }
    }

    public void AddToSonarrCache(int tvdbId, int seriesId, string titleSlug, string baseUrl)
    {
        _sonarrCache[tvdbId] = (seriesId, titleSlug, baseUrl, DateTimeOffset.UtcNow.Add(CacheTtl));
    }

    public void AddToRadarrCache(int tmdbId, int movieId, string baseUrl)
    {
        _radarrCache[tmdbId] = (movieId, baseUrl, DateTimeOffset.UtcNow.Add(CacheTtl));
    }

    public void ClearCache()
    {
        _sonarrCache.Clear();
        _radarrCache.Clear();
        _sonarrTitleCache.Clear();
        _radarrTitleCache.Clear();
    }
}
