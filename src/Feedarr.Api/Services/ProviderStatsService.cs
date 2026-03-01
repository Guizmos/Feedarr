using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.ExternalProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

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
        ExternalProviderKeys.ComicVine,
        ExternalProviderKeys.MusicBrainz,
        ExternalProviderKeys.OpenLibrary,
        ExternalProviderKeys.Rawg
    };

    private readonly IProviderStatsStore _store;
    private readonly ILogger<ProviderStatsService> _logger;
    private readonly ProviderStatsFlushOptions _flushOptions;
    private readonly object _loadLock = new();
    private readonly ConcurrentDictionary<string, CounterState> _counters = new(StringComparer.OrdinalIgnoreCase);

    private volatile bool _loaded;
    private long _lastLoadFailureTicks;
    private int _flushInProgress;

    private const int LoadRetryDebounceMs = 30_000;

    public ProviderStatsService(
        IProviderStatsStore store,
        ILogger<ProviderStatsService>? logger = null,
        IOptions<ProviderStatsFlushOptions>? flushOptions = null)
    {
        _store = store;
        _logger = logger ?? NullLogger<ProviderStatsService>.Instance;
        _flushOptions = flushOptions?.Value ?? new ProviderStatsFlushOptions();
    }

    public void RecordTmdb(bool ok)
        => RecordTmdb(ok, 0);

    public void RecordTmdb(bool ok, long elapsedMs)
        => RecordProviderCall(ExternalProviderKeys.Tmdb, ok, elapsedMs);

    public void RecordFanart(bool ok)
        => RecordFanart(ok, 0);

    public void RecordFanart(bool ok, long elapsedMs)
        => RecordProviderCall(ExternalProviderKeys.Fanart, ok, elapsedMs);

    public void RecordIgdb(bool ok)
        => RecordIgdb(ok, 0);

    public void RecordIgdb(bool ok, long elapsedMs)
        => RecordProviderCall(ExternalProviderKeys.Igdb, ok, elapsedMs);

    public void RecordTvmaze(bool ok)
        => RecordTvmaze(ok, 0);

    public void RecordTvmaze(bool ok, long elapsedMs)
        => RecordProviderCall(ExternalProviderKeys.Tvmaze, ok, elapsedMs);

    public void RecordExternal(string providerKey, bool ok, long elapsedMs = 0)
        => RecordProviderCall(providerKey, ok, elapsedMs);

    public void RecordIndexerQuery(bool ok)
    {
        EnsureLoaded();
        AddDelta("indexer_queries", 1);
        if (!ok)
            AddDelta("indexer_failures", 1);
    }

    public void RecordSyncJob(bool ok)
    {
        EnsureLoaded();
        AddDelta("sync_jobs", 1);
        if (!ok)
            AddDelta("sync_failures", 1);
    }

    public ProviderStatsSnapshot Snapshot()
    {
        EnsureLoaded();
        return new ProviderStatsSnapshot(
            BuildProviderStats("tmdb"),
            BuildProviderStats("tvmaze"),
            BuildProviderStats("fanart"),
            BuildProviderStats("igdb"));
    }

    public IReadOnlyDictionary<string, ProviderStats> SnapshotByProvider()
    {
        EnsureLoaded();
        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in _counters.Keys)
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
            result[provider] = BuildProviderStats(provider);
        }

        return result;
    }

    public IndexerStatsSnapshot IndexerSnapshot()
    {
        EnsureLoaded();
        return new IndexerStatsSnapshot(
            ReadCounter("indexer_queries"),
            ReadCounter("indexer_failures"),
            ReadCounter("sync_jobs"),
            ReadCounter("sync_failures"));
    }

    public async Task<int> FlushAsync(CancellationToken ct = default)
    {
        EnsureLoaded();
        if (!_loaded)
            return 0;

        if (Interlocked.CompareExchange(ref _flushInProgress, 1, 0) != 0)
            return 0;

        try
        {
            var maxBatchSize = Math.Clamp(_flushOptions.MaxBatchSize, 1, 10_000);
            var batch = new List<ProviderStatDelta>(maxBatchSize);

            foreach (var pair in _counters.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();

                if (pair.Value.TryCaptureDelta(pair.Key, out var delta))
                    batch.Add(delta);

                if (batch.Count >= maxBatchSize)
                    break;
            }

            if (batch.Count == 0)
                return 0;

            await _store.IncrementProviderStatsBatchAsync(batch, ct);

            foreach (var row in batch)
            {
                if (_counters.TryGetValue(row.Key, out var state))
                    state.MarkFlushed(row.TotalAfterIncrement);
            }

            return batch.Count;
        }
        finally
        {
            Volatile.Write(ref _flushInProgress, 0);
        }
    }

    private void RecordProviderCall(string providerKey, bool ok, long elapsedMs)
    {
        EnsureLoaded();

        var key = NormalizeProviderKey(providerKey);
        if (string.IsNullOrWhiteSpace(key))
            return;

        AddDelta($"{key}_calls", 1);

        var safeMs = Math.Max(0, elapsedMs);
        if (safeMs > 0)
            AddDelta($"{key}_total_ms", safeMs);

        if (!ok)
            AddDelta($"{key}_failures", 1);
    }

    private void EnsureLoaded()
    {
        if (_loaded)
            return;

        lock (_loadLock)
        {
            if (_loaded)
                return;

            if (_lastLoadFailureTicks > 0 &&
                Environment.TickCount64 - _lastLoadFailureTicks < LoadRetryDebounceMs)
            {
                return;
            }

            try
            {
                var all = _store.GetAll();
                foreach (var pair in all)
                {
                    GetCounter(pair.Key).InitializeBaseline(pair.Value);
                }

                _loaded = true;
                _lastLoadFailureTicks = 0;
            }
            catch (Exception ex)
            {
                _lastLoadFailureTicks = Environment.TickCount64;
                _logger.LogWarning(
                    ex,
                    "ProviderStatsService failed to load stats from DB; will retry after {DebounceMs}ms",
                    LoadRetryDebounceMs);
            }
        }
    }

    private ProviderStats BuildProviderStats(string providerKey)
    {
        var calls = ReadCounter($"{providerKey}_calls");
        var failures = ReadCounter($"{providerKey}_failures");
        var totalMs = ReadCounter($"{providerKey}_total_ms");
        var avgMs = calls > 0 ? (long)((double)totalMs / calls) : 0;
        return new ProviderStats(calls, failures, avgMs);
    }

    private void AddDelta(string key, long delta)
    {
        if (delta <= 0)
            return;

        GetCounter(key).Add(delta);
    }

    private long ReadCounter(string key)
    {
        return _counters.TryGetValue(key, out var state)
            ? state.ReadTotal()
            : 0;
    }

    private CounterState GetCounter(string key)
        => _counters.GetOrAdd(key, static _ => new CounterState());

    private static string NormalizeProviderKey(string? providerKey)
    {
        return string.IsNullOrWhiteSpace(providerKey)
            ? ""
            : providerKey.Trim().ToLowerInvariant();
    }

    private sealed class CounterState
    {
        private long _total;
        private long _lastFlushed;
        private int _initialized;

        public void Add(long delta)
            => Interlocked.Add(ref _total, delta);

        public void InitializeBaseline(long baseline)
        {
            if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
                return;

            if (baseline != 0)
                Interlocked.Add(ref _total, baseline);

            Volatile.Write(ref _lastFlushed, baseline);
        }

        public long ReadTotal()
            => Volatile.Read(ref _total);

        public bool TryCaptureDelta(string key, out ProviderStatDelta delta)
        {
            var total = Volatile.Read(ref _total);
            var lastFlushed = Volatile.Read(ref _lastFlushed);
            var pending = total - lastFlushed;

            if (pending <= 0)
            {
                delta = default!;
                return false;
            }

            delta = new ProviderStatDelta(key, pending, total);
            return true;
        }

        public void MarkFlushed(long totalAfterIncrement)
            => Volatile.Write(ref _lastFlushed, totalAfterIncrement);
    }
}
