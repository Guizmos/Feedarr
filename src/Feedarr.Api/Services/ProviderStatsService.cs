using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services.ExternalProviders;

namespace Feedarr.Api.Services;

public sealed record ProviderStats(long Calls, long Failures, long AvgMs);

public sealed record ProviderStatsSnapshot(
    ProviderStats Tmdb,
    ProviderStats Tvmaze,
    ProviderStats Fanart,
    ProviderStats Igdb
);

public sealed record IndexerStatsSnapshot(
    long Queries,
    long Failures,
    long SyncJobs,
    long SyncFailures
);

public sealed class ProviderStatsService
{
    private static readonly HashSet<string> KnownMetadataProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        ExternalProviderKeys.Tmdb,
        ExternalProviderKeys.Tvmaze,
        ExternalProviderKeys.Fanart,
        ExternalProviderKeys.Igdb,
        ExternalProviderKeys.Jikan,
        ExternalProviderKeys.GoogleBooks,
        ExternalProviderKeys.TheAudioDb,
        ExternalProviderKeys.ComicVine
    };

    private readonly StatsRepository _repo;
    private readonly object _lock = new();

    // In-memory cache for performance (flushed periodically)
    private long _tmdbCalls;
    private long _tmdbFailures;
    private long _tvmazeCalls;
    private long _tvmazeFailures;
    private long _fanartCalls;
    private long _fanartFailures;
    private long _igdbCalls;
    private long _igdbFailures;
    private long _tmdbTotalMs;
    private long _tvmazeTotalMs;
    private long _fanartTotalMs;
    private long _igdbTotalMs;
    private long _indexerQueries;
    private long _indexerFailures;
    private long _syncJobs;
    private long _syncFailures;
    private volatile bool _loaded;
    private long _lastLoadFailureTicks;
    private const int LoadRetryDebounceMs = 30_000;

    public ProviderStatsService(StatsRepository repo)
    {
        _repo = repo;
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;

        // If a previous load failed, wait before retrying
        if (_lastLoadFailureTicks > 0
            && Environment.TickCount64 - _lastLoadFailureTicks < LoadRetryDebounceMs)
            return;

        lock (_lock)
        {
            if (_loaded) return;
            try
            {
                var all = _repo.GetAll();
                _tmdbCalls = all.GetValueOrDefault("tmdb_calls", 0);
                _tmdbFailures = all.GetValueOrDefault("tmdb_failures", 0);
                _tvmazeCalls = all.GetValueOrDefault("tvmaze_calls", 0);
                _tvmazeFailures = all.GetValueOrDefault("tvmaze_failures", 0);
                _fanartCalls = all.GetValueOrDefault("fanart_calls", 0);
                _fanartFailures = all.GetValueOrDefault("fanart_failures", 0);
                _igdbCalls = all.GetValueOrDefault("igdb_calls", 0);
                _igdbFailures = all.GetValueOrDefault("igdb_failures", 0);
                _tmdbTotalMs = all.GetValueOrDefault("tmdb_total_ms", 0);
                _tvmazeTotalMs = all.GetValueOrDefault("tvmaze_total_ms", 0);
                _fanartTotalMs = all.GetValueOrDefault("fanart_total_ms", 0);
                _igdbTotalMs = all.GetValueOrDefault("igdb_total_ms", 0);
                _indexerQueries = all.GetValueOrDefault("indexer_queries", 0);
                _indexerFailures = all.GetValueOrDefault("indexer_failures", 0);
                _syncJobs = all.GetValueOrDefault("sync_jobs", 0);
                _syncFailures = all.GetValueOrDefault("sync_failures", 0);
                _loaded = true;
                _lastLoadFailureTicks = 0;
            }
            catch
            {
                // DB not ready yet — keep _loaded false so we retry after debounce
                _lastLoadFailureTicks = Environment.TickCount64;
            }
        }
    }

    public void RecordTmdb(bool ok)
    {
        RecordTmdb(ok, 0);
    }

    public void RecordTmdb(bool ok, long elapsedMs)
    {
        EnsureLoaded();
        var safeMs = Math.Max(0, elapsedMs);
        Interlocked.Increment(ref _tmdbCalls);
        _repo.Increment("tmdb_calls");
        if (safeMs > 0)
        {
            Interlocked.Add(ref _tmdbTotalMs, safeMs);
            _repo.Increment("tmdb_total_ms", safeMs);
        }
        if (!ok)
        {
            Interlocked.Increment(ref _tmdbFailures);
            _repo.Increment("tmdb_failures");
        }
    }

    public void RecordFanart(bool ok)
    {
        RecordFanart(ok, 0);
    }

    public void RecordFanart(bool ok, long elapsedMs)
    {
        EnsureLoaded();
        var safeMs = Math.Max(0, elapsedMs);
        Interlocked.Increment(ref _fanartCalls);
        _repo.Increment("fanart_calls");
        if (safeMs > 0)
        {
            Interlocked.Add(ref _fanartTotalMs, safeMs);
            _repo.Increment("fanart_total_ms", safeMs);
        }
        if (!ok)
        {
            Interlocked.Increment(ref _fanartFailures);
            _repo.Increment("fanart_failures");
        }
    }

    public void RecordIgdb(bool ok)
    {
        RecordIgdb(ok, 0);
    }

    public void RecordIgdb(bool ok, long elapsedMs)
    {
        EnsureLoaded();
        var safeMs = Math.Max(0, elapsedMs);
        Interlocked.Increment(ref _igdbCalls);
        _repo.Increment("igdb_calls");
        if (safeMs > 0)
        {
            Interlocked.Add(ref _igdbTotalMs, safeMs);
            _repo.Increment("igdb_total_ms", safeMs);
        }
        if (!ok)
        {
            Interlocked.Increment(ref _igdbFailures);
            _repo.Increment("igdb_failures");
        }
    }

    public void RecordIndexerQuery(bool ok)
    {
        EnsureLoaded();
        Interlocked.Increment(ref _indexerQueries);
        _repo.Increment("indexer_queries");
        if (!ok)
        {
            Interlocked.Increment(ref _indexerFailures);
            _repo.Increment("indexer_failures");
        }
    }

    public void RecordSyncJob(bool ok)
    {
        EnsureLoaded();
        Interlocked.Increment(ref _syncJobs);
        _repo.Increment("sync_jobs");
        if (!ok)
        {
            Interlocked.Increment(ref _syncFailures);
            _repo.Increment("sync_failures");
        }
    }

    public ProviderStatsSnapshot Snapshot()
    {
        EnsureLoaded();
        var tmdbCalls = Interlocked.Read(ref _tmdbCalls);
        var tmdbFailures = Interlocked.Read(ref _tmdbFailures);
        var tmdbTotalMs = Interlocked.Read(ref _tmdbTotalMs);
        var tvmazeCalls = Interlocked.Read(ref _tvmazeCalls);
        var tvmazeFailures = Interlocked.Read(ref _tvmazeFailures);
        var tvmazeTotalMs = Interlocked.Read(ref _tvmazeTotalMs);
        var fanartCalls = Interlocked.Read(ref _fanartCalls);
        var fanartFailures = Interlocked.Read(ref _fanartFailures);
        var fanartTotalMs = Interlocked.Read(ref _fanartTotalMs);
        var igdbCalls = Interlocked.Read(ref _igdbCalls);
        var igdbFailures = Interlocked.Read(ref _igdbFailures);
        var igdbTotalMs = Interlocked.Read(ref _igdbTotalMs);

        var tmdbAvgMs = tmdbCalls > 0 ? (long)((double)tmdbTotalMs / tmdbCalls) : 0;
        var tvmazeAvgMs = tvmazeCalls > 0 ? (long)((double)tvmazeTotalMs / tvmazeCalls) : 0;
        var fanartAvgMs = fanartCalls > 0 ? (long)((double)fanartTotalMs / fanartCalls) : 0;
        var igdbAvgMs = igdbCalls > 0 ? (long)((double)igdbTotalMs / igdbCalls) : 0;

        var tmdb = new ProviderStats(tmdbCalls, tmdbFailures, tmdbAvgMs);
        var tvmaze = new ProviderStats(tvmazeCalls, tvmazeFailures, tvmazeAvgMs);
        var fanart = new ProviderStats(fanartCalls, fanartFailures, fanartAvgMs);
        var igdb = new ProviderStats(igdbCalls, igdbFailures, igdbAvgMs);

        return new ProviderStatsSnapshot(tmdb, tvmaze, fanart, igdb);
    }

    public void RecordExternal(string providerKey, bool ok, long elapsedMs = 0)
    {
        EnsureLoaded();
        var key = NormalizeProviderKey(providerKey);
        if (string.IsNullOrWhiteSpace(key))
            return;

        var safeMs = Math.Max(0, elapsedMs);

        _repo.Increment($"{key}_calls");
        if (safeMs > 0)
            _repo.Increment($"{key}_total_ms", safeMs);
        if (!ok)
            _repo.Increment($"{key}_failures");

        // Keep legacy in-memory counters consistent when generic recording is used
        // with existing providers.
        switch (key)
        {
            case "tmdb":
                Interlocked.Increment(ref _tmdbCalls);
                if (safeMs > 0) Interlocked.Add(ref _tmdbTotalMs, safeMs);
                if (!ok) Interlocked.Increment(ref _tmdbFailures);
                break;
            case "tvmaze":
                Interlocked.Increment(ref _tvmazeCalls);
                if (safeMs > 0) Interlocked.Add(ref _tvmazeTotalMs, safeMs);
                if (!ok) Interlocked.Increment(ref _tvmazeFailures);
                break;
            case "fanart":
                Interlocked.Increment(ref _fanartCalls);
                if (safeMs > 0) Interlocked.Add(ref _fanartTotalMs, safeMs);
                if (!ok) Interlocked.Increment(ref _fanartFailures);
                break;
            case "igdb":
                Interlocked.Increment(ref _igdbCalls);
                if (safeMs > 0) Interlocked.Add(ref _igdbTotalMs, safeMs);
                if (!ok) Interlocked.Increment(ref _igdbFailures);
                break;
        }
    }

    public IReadOnlyDictionary<string, ProviderStats> SnapshotByProvider()
    {
        EnsureLoaded();
        var all = _repo.GetAll();
        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in all.Keys)
        {
            if (key.EndsWith("_calls", StringComparison.OrdinalIgnoreCase))
                providers.Add(key[..^"_calls".Length]);
            else if (key.EndsWith("_failures", StringComparison.OrdinalIgnoreCase))
                providers.Add(key[..^"_failures".Length]);
            else if (key.EndsWith("_total_ms", StringComparison.OrdinalIgnoreCase))
                providers.Add(key[..^"_total_ms".Length]);
        }

        var result = new Dictionary<string, ProviderStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers
            .Where(p => KnownMetadataProviders.Contains(p))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var calls = all.GetValueOrDefault($"{provider}_calls", 0);
            var failures = all.GetValueOrDefault($"{provider}_failures", 0);
            var totalMs = all.GetValueOrDefault($"{provider}_total_ms", 0);
            var avgMs = calls > 0 ? (long)((double)totalMs / calls) : 0;
            result[provider] = new ProviderStats(calls, failures, avgMs);
        }

        return result;
    }

    public void RecordTvmaze(bool ok)
    {
        RecordTvmaze(ok, 0);
    }

    public void RecordTvmaze(bool ok, long elapsedMs)
    {
        EnsureLoaded();
        var safeMs = Math.Max(0, elapsedMs);
        Interlocked.Increment(ref _tvmazeCalls);
        _repo.Increment("tvmaze_calls");
        if (safeMs > 0)
        {
            Interlocked.Add(ref _tvmazeTotalMs, safeMs);
            _repo.Increment("tvmaze_total_ms", safeMs);
        }
        if (!ok)
        {
            Interlocked.Increment(ref _tvmazeFailures);
            _repo.Increment("tvmaze_failures");
        }
    }

    public IndexerStatsSnapshot IndexerSnapshot()
    {
        EnsureLoaded();
        return new IndexerStatsSnapshot(
            Interlocked.Read(ref _indexerQueries),
            Interlocked.Read(ref _indexerFailures),
            Interlocked.Read(ref _syncJobs),
            Interlocked.Read(ref _syncFailures)
        );
    }

    private static string NormalizeProviderKey(string? providerKey)
    {
        return string.IsNullOrWhiteSpace(providerKey)
            ? ""
            : providerKey.Trim().ToLowerInvariant();
    }
}
