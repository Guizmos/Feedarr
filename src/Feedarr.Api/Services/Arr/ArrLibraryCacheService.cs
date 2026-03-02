using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Services.Arr;

/// <summary>
/// Snapshot of title-cache hit/miss/eviction counters.
/// </summary>
public sealed record ArrLibraryCacheMetrics(
    long SonarrTitleHits,
    long SonarrTitleMisses,
    long RadarrTitleHits,
    long RadarrTitleMisses,
    long Evictions);

public sealed class ArrLibraryCacheService : IDisposable
{
    private readonly ArrApplicationRepository _repo;
    private readonly SonarrClient _sonarr;
    private readonly RadarrClient _radarr;
    private readonly ILogger<ArrLibraryCacheService> _log;
    private readonly ArrLibraryCacheOptions _opts;

    // ID caches — manual 10-min TTL, ConcurrentDictionary (fast O(1) lookup by int key)
    private readonly ConcurrentDictionary<int, (int seriesId, string titleSlug, string baseUrl, DateTimeOffset expiresAt)> _sonarrCache = new();
    private readonly ConcurrentDictionary<int, (int movieId, string baseUrl, DateTimeOffset expiresAt)> _radarrCache = new();

    // Title caches — bounded MemoryCache with sliding+absolute TTL and size limit
    private readonly MemoryCache _sonarrTitleCache;
    private readonly MemoryCache _radarrTitleCache;

    // Anti-stampede: only one refresh per app type runs at a time
    private readonly SemaphoreSlim _sonarrRefreshLock = new(1, 1);
    private readonly SemaphoreSlim _radarrRefreshLock = new(1, 1);

    // Metrics — updated with Interlocked for thread-safety
    private long _sonarrTitleHits;
    private long _sonarrTitleMisses;
    private long _radarrTitleHits;
    private long _radarrTitleMisses;
    private long _evictions;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private static string NormalizeTitle(string? title) =>
        TitleNormalizer.NormalizeTitleStrict(title);

    public ArrLibraryCacheService(
        ArrApplicationRepository repo,
        SonarrClient sonarr,
        RadarrClient radarr,
        ILogger<ArrLibraryCacheService> log,
        IOptions<ArrLibraryCacheOptions>? opts = null)
    {
        _repo = repo;
        _sonarr = sonarr;
        _radarr = radarr;
        _log = log;
        _opts = opts?.Value ?? new ArrLibraryCacheOptions();

        _sonarrTitleCache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = _opts.MaxTitleEntries,
        });
        _radarrTitleCache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = _opts.MaxTitleEntries,
        });
    }

    private MemoryCacheEntryOptions BuildTitleEntryOptions() =>
        new MemoryCacheEntryOptions()
            .SetSize(1)
            .SetSlidingExpiration(TimeSpan.FromMinutes(_opts.SlidingExpirationMinutes))
            .SetAbsoluteExpiration(TimeSpan.FromHours(_opts.AbsoluteExpirationHours))
            .RegisterPostEvictionCallback(OnTitleEntryEvicted);

    private void OnTitleEntryEvicted(object key, object? value, EvictionReason reason, object? state)
    {
        // Don't count replacements (e.g. during cache refresh) as evictions
        if (reason != EvictionReason.Replaced)
            Interlocked.Increment(ref _evictions);
    }

    private void AddSonarrTitleKeys(string? title, SonarrTitleEntry entry, MemoryCacheEntryOptions options)
    {
        var variants = TitleNormalizer.BuildTitleVariants(title);
        foreach (var key in variants)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            _sonarrTitleCache.Set(key, entry, options);
        }
    }

    private void AddRadarrTitleKeys(string? title, RadarrTitleEntry entry, MemoryCacheEntryOptions options)
    {
        var variants = TitleNormalizer.BuildTitleVariants(title);
        foreach (var key in variants)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            _radarrTitleCache.Set(key, entry, options);
        }
    }

    private bool IsSonarrCacheStale()
    {
        var now = DateTimeOffset.UtcNow;
        return !_sonarrCache.Values.Any(e => e.expiresAt > now);
    }

    private bool IsRadarrCacheStale()
    {
        var now = DateTimeOffset.UtcNow;
        return !_radarrCache.Values.Any(e => e.expiresAt > now);
    }

    public async Task RefreshSonarrCacheAsync(CancellationToken ct)
    {
        // Fast path: if already fresh, skip acquiring the lock
        if (!IsSonarrCacheStale()) return;

        await _sonarrRefreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check inside lock: another thread may have refreshed while we waited
            if (!IsSonarrCacheStale()) return;

            EvictExpiredIdCaches();

            var app = _repo.GetDefault("sonarr");
            if (app is null || string.IsNullOrWhiteSpace(app.ApiKeyEncrypted)) return;

            try
            {
                var series = await _sonarr.GetAllSeriesAsync(app.BaseUrl, app.ApiKeyEncrypted, ct).ConfigureAwait(false);                var expiresAt = DateTimeOffset.UtcNow.Add(CacheTtl);

                // Evict all title entries before repopulating to avoid stale keys
                _sonarrTitleCache.Compact(1.0);

                // Build entry options once per batch (same TTL for all entries)
                var entryOptions = BuildTitleEntryOptions();

                foreach (var s in series)
                {
                    if (s.TvdbId > 0)
                        _sonarrCache[s.TvdbId] = (s.Id, s.TitleSlug, app.BaseUrl, expiresAt);

                    var entry = new SonarrTitleEntry(
                        s.TvdbId > 0 ? s.TvdbId : null,
                        s.Id,
                        s.TitleSlug,
                        app.BaseUrl);

                    AddSonarrTitleKeys(s.Title, entry, entryOptions);

                    if (s.AlternateTitles is { Count: > 0 })
                    {
                        foreach (var alt in s.AlternateTitles)
                            AddSonarrTitleKeys(alt.Title, entry, entryOptions);
                    }
                }

                _log.LogDebug("Sonarr cache refreshed: {Count} series", series.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Failed to refresh Sonarr cache");
            }
        }
        finally
        {
            _sonarrRefreshLock.Release();
        }
    }

    public async Task RefreshRadarrCacheAsync(CancellationToken ct)
    {
        // Fast path: if already fresh, skip acquiring the lock
        if (!IsRadarrCacheStale()) return;

        await _radarrRefreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check inside lock
            if (!IsRadarrCacheStale()) return;

            EvictExpiredIdCaches();

            var app = _repo.GetDefault("radarr");
            if (app is null || string.IsNullOrWhiteSpace(app.ApiKeyEncrypted)) return;

            try
            {
                var movies = await _radarr.GetAllMoviesAsync(app.BaseUrl, app.ApiKeyEncrypted, ct).ConfigureAwait(false);                var expiresAt = DateTimeOffset.UtcNow.Add(CacheTtl);

                _radarrTitleCache.Compact(1.0);

                var entryOptions = BuildTitleEntryOptions();

                foreach (var m in movies)
                {
                    if (m.TmdbId > 0)
                        _radarrCache[m.TmdbId] = (m.Id, app.BaseUrl, expiresAt);

                    var entry = new RadarrTitleEntry(
                        m.TmdbId > 0 ? m.TmdbId : null,
                        m.Id,
                        app.BaseUrl);

                    AddRadarrTitleKeys(m.Title, entry, entryOptions);

                    if (!string.IsNullOrWhiteSpace(m.OriginalTitle) && m.OriginalTitle != m.Title)
                        AddRadarrTitleKeys(m.OriginalTitle, entry, entryOptions);

                    if (m.AlternateTitles is { Count: > 0 })
                    {
                        foreach (var alt in m.AlternateTitles)
                            AddRadarrTitleKeys(alt.Title, entry, entryOptions);
                    }
                }

                _log.LogDebug("Radarr cache refreshed: {Count} movies", movies.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Failed to refresh Radarr cache");
            }
        }
        finally
        {
            _radarrRefreshLock.Release();
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
                var openUrl = _radarr.BuildOpenUrl(entry.baseUrl, tmdbId);
                return (true, entry.movieId, openUrl);
            }
            _radarrCache.TryRemove(tmdbId, out _);
        }
        return (false, null, null);
    }

    /// <summary>
    /// Fallback: check Sonarr by title when tvdbId is not available.
    /// </summary>
    public (bool exists, int? seriesId, string? openUrl, int? foundTvdbId) CheckSonarrExistsByTitle(string? title)
    {
        var variants = TitleNormalizer.BuildTitleVariants(title);
        if (variants.Length == 0)
            return (false, null, null, null);

        foreach (var key in variants)
        {
            if (_sonarrTitleCache.TryGetValue<SonarrTitleEntry>(key, out var entry) && entry is not null)
            {
                Interlocked.Increment(ref _sonarrTitleHits);
                var openUrl = string.IsNullOrWhiteSpace(entry.TitleSlug)
                    ? null
                    : _sonarr.BuildOpenUrl(entry.BaseUrl, entry.TitleSlug);
                return (true, entry.SeriesId, openUrl, entry.TvdbId);
            }
        }

        Interlocked.Increment(ref _sonarrTitleMisses);
        return (false, null, null, null);
    }

    /// <summary>
    /// Fallback: check Radarr by title when tmdbId is not available.
    /// </summary>
    public (bool exists, int? movieId, string? openUrl, int? foundTmdbId) CheckRadarrExistsByTitle(string? title)
    {
        var variants = TitleNormalizer.BuildTitleVariants(title);
        if (variants.Length == 0)
            return (false, null, null, null);

        foreach (var key in variants)
        {
            if (_radarrTitleCache.TryGetValue<RadarrTitleEntry>(key, out var entry) && entry is not null)
            {
                Interlocked.Increment(ref _radarrTitleHits);
                var openUrl = entry.TmdbId.HasValue
                    ? _radarr.BuildOpenUrl(entry.BaseUrl, entry.TmdbId.Value)
                    : null;
                return (true, entry.MovieId, openUrl, entry.TmdbId);
            }
        }

        Interlocked.Increment(ref _radarrTitleMisses);
        return (false, null, null, null);
    }

    public async Task<(bool exists, int? seriesId, string? openUrl)> CheckSonarrExistsWithRefreshAsync(
        int tvdbId, CancellationToken ct)
    {
        var cached = CheckSonarrExists(tvdbId);
        if (cached.exists) return cached;

        if (IsSonarrCacheStale())
        {
            await RefreshSonarrCacheAsync(ct).ConfigureAwait(false);            return CheckSonarrExists(tvdbId);
        }

        return (false, null, null);
    }

    public async Task<(bool exists, int? movieId, string? openUrl)> CheckRadarrExistsWithRefreshAsync(
        int tmdbId, CancellationToken ct)
    {
        var cached = CheckRadarrExists(tmdbId);
        if (cached.exists) return cached;

        if (IsRadarrCacheStale())
        {
            await RefreshRadarrCacheAsync(ct).ConfigureAwait(false);            return CheckRadarrExists(tmdbId);
        }

        return (false, null, null);
    }

    public async Task EnsureSonarrCacheFreshAsync(CancellationToken ct)
    {
        if (IsSonarrCacheStale())
            await RefreshSonarrCacheAsync(ct).ConfigureAwait(false);    }

    public async Task EnsureRadarrCacheFreshAsync(CancellationToken ct)
    {
        if (IsRadarrCacheStale())
            await RefreshRadarrCacheAsync(ct).ConfigureAwait(false);    }

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
        _sonarrTitleCache.Compact(1.0);
        _radarrTitleCache.Compact(1.0);
    }

    /// <summary>
    /// Removes expired entries from the ID caches and triggers MemoryCache cleanup
    /// for title caches. Returns the count of ID-cache entries evicted.
    /// </summary>
    public int EvictExpired()
    {
        var evicted = EvictExpiredIdCaches();
        // Compact(0) triggers MemoryCache's internal expired-entry scan without
        // removing any non-expired entries beyond what is already due.
        _sonarrTitleCache.Compact(0);
        _radarrTitleCache.Compact(0);
        return evicted;
    }

    /// <summary>Returns a snapshot of title-cache hit/miss/eviction metrics.</summary>
    public ArrLibraryCacheMetrics GetMetrics() => new(
        Interlocked.Read(ref _sonarrTitleHits),
        Interlocked.Read(ref _sonarrTitleMisses),
        Interlocked.Read(ref _radarrTitleHits),
        Interlocked.Read(ref _radarrTitleMisses),
        Interlocked.Read(ref _evictions));

    public void Dispose()
    {
        _sonarrTitleCache.Dispose();
        _radarrTitleCache.Dispose();
        _sonarrRefreshLock.Dispose();
        _radarrRefreshLock.Dispose();
    }

    private int EvictExpiredIdCaches()
    {
        var now = DateTimeOffset.UtcNow;
        var evicted = 0;

        foreach (var kvp in _sonarrCache)
        {
            if (kvp.Value.expiresAt <= now && _sonarrCache.TryRemove(kvp.Key, out _))
                evicted++;
        }

        foreach (var kvp in _radarrCache)
        {
            if (kvp.Value.expiresAt <= now && _radarrCache.TryRemove(kvp.Key, out _))
                evicted++;
        }

        return evicted;
    }

    private sealed record SonarrTitleEntry(int? TvdbId, int SeriesId, string TitleSlug, string BaseUrl);
    private sealed record RadarrTitleEntry(int? TmdbId, int MovieId, string BaseUrl);
}
