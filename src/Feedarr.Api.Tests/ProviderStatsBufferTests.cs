using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class ProviderStatsBufferTests
{
    [Fact]
    public void Increment_InMemoryVisibleImmediately()
    {
        var store = new FakeProviderStatsStore();
        var service = CreateService(store);

        service.RecordTmdb(true, 120);

        var snapshot = service.Snapshot();
        Assert.Equal(1, snapshot.Tmdb.Calls);
        Assert.Equal(0, snapshot.Tmdb.Failures);
        Assert.Equal(120, snapshot.Tmdb.AvgMs);

        var byProvider = service.SnapshotByProvider();
        Assert.Equal(1, byProvider["tmdb"].Calls);
    }

    [Fact]
    public async Task Flush_WritesDeltasOnce()
    {
        var store = new FakeProviderStatsStore();
        var service = CreateService(store);

        service.RecordTmdb(true, 100);
        service.RecordTmdb(false, 50);

        var firstFlush = await service.FlushAsync();
        var secondFlush = await service.FlushAsync();

        Assert.Equal(3, firstFlush);
        Assert.Equal(0, secondFlush);
        Assert.Equal(2, store.GetPersistedValue("tmdb_calls"));
        Assert.Equal(1, store.GetPersistedValue("tmdb_failures"));
        Assert.Equal(150, store.GetPersistedValue("tmdb_total_ms"));
    }

    [Fact]
    public async Task Flush_Failure_RetriesWithoutLoss()
    {
        var store = new FakeProviderStatsStore { FailNextFlush = true };
        var service = CreateService(store);

        service.RecordExternal("jikan", ok: false, elapsedMs: 70);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.FlushAsync());

        Assert.Equal(0, store.GetPersistedValue("jikan_calls"));
        Assert.Equal(0, store.GetPersistedValue("jikan_failures"));
        Assert.Equal(0, store.GetPersistedValue("jikan_total_ms"));

        var flushed = await service.FlushAsync();

        Assert.Equal(3, flushed);
        Assert.Equal(1, store.GetPersistedValue("jikan_calls"));
        Assert.Equal(1, store.GetPersistedValue("jikan_failures"));
        Assert.Equal(70, store.GetPersistedValue("jikan_total_ms"));
    }

    [Fact]
    public async Task ConcurrentIncrements_NoLostUpdates()
    {
        var store = new FakeProviderStatsStore();
        var service = CreateService(store);
        const int workers = 24;
        const int iterationsPerWorker = 250;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, workers),
            async (_, _) =>
            {
                for (var i = 0; i < iterationsPerWorker; i++)
                    service.RecordTmdb(ok: false, elapsedMs: 5);

                await Task.CompletedTask;
            });

        var snapshot = service.Snapshot();
        var expected = workers * iterationsPerWorker;
        Assert.Equal(expected, snapshot.Tmdb.Calls);
        Assert.Equal(expected, snapshot.Tmdb.Failures);
        Assert.Equal(5, snapshot.Tmdb.AvgMs);

        await service.FlushAsync();
        Assert.Equal(expected, store.GetPersistedValue("tmdb_calls"));
        Assert.Equal(expected, store.GetPersistedValue("tmdb_failures"));
        Assert.Equal(expected * 5L, store.GetPersistedValue("tmdb_total_ms"));
    }

    [Fact]
    public async Task StopAsync_FlushesFinal()
    {
        var store = new FakeProviderStatsStore();
        var service = CreateService(
            store,
            new ProviderStatsFlushOptions
            {
                EnableFlush = true,
                FlushIntervalSeconds = 60,
                MaxBatchSize = 100
            });
        var hosted = new ProviderStatsFlushHostedService(
            service,
            OptionsFactory.Create(new ProviderStatsFlushOptions
            {
                EnableFlush = true,
                FlushIntervalSeconds = 60,
                MaxBatchSize = 100
            }),
            NullLogger<ProviderStatsFlushHostedService>.Instance);

        await hosted.StartAsync(CancellationToken.None);
        service.RecordExternal("rawg", ok: true, elapsedMs: 33);
        await hosted.StopAsync(CancellationToken.None);

        Assert.Equal(1, store.GetPersistedValue("rawg_calls"));
        Assert.Equal(33, store.GetPersistedValue("rawg_total_ms"));
    }

    [Fact]
    public async Task BatchLimit_RespectsMaxBatchSize()
    {
        var store = new FakeProviderStatsStore();
        var service = CreateService(
            store,
            new ProviderStatsFlushOptions
            {
                EnableFlush = true,
                FlushIntervalSeconds = 5,
                MaxBatchSize = 2
            });

        service.RecordExternal("tmdb", ok: true);
        service.RecordExternal("fanart", ok: true);
        service.RecordExternal("igdb", ok: true);

        var firstFlush = await service.FlushAsync();
        Assert.Equal(2, firstFlush);
        Assert.Equal(2, store.BatchSizes.Single());

        var secondFlush = await service.FlushAsync();
        Assert.Equal(1, secondFlush);
        Assert.Equal(new[] { 2, 1 }, store.BatchSizes);
    }

    private static ProviderStatsService CreateService(
        FakeProviderStatsStore store,
        ProviderStatsFlushOptions? options = null)
    {
        return new ProviderStatsService(
            store,
            NullLogger<ProviderStatsService>.Instance,
            OptionsFactory.Create(options ?? new ProviderStatsFlushOptions()));
    }

    private sealed class FakeProviderStatsStore : IProviderStatsStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, long> _values = new(StringComparer.OrdinalIgnoreCase);

        public bool FailNextFlush { get; set; }
        public List<int> BatchSizes { get; } = new();

        public Dictionary<string, long> GetAll()
        {
            lock (_gate)
            {
                return new Dictionary<string, long>(_values, StringComparer.OrdinalIgnoreCase);
            }
        }

        public Task IncrementProviderStatsBatchAsync(IReadOnlyList<ProviderStatDelta> rows, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            lock (_gate)
            {
                if (FailNextFlush)
                {
                    FailNextFlush = false;
                    throw new InvalidOperationException("Simulated stats flush failure");
                }

                BatchSizes.Add(rows.Count);
                foreach (var row in rows)
                    _values[row.Key] = _values.GetValueOrDefault(row.Key) + row.Delta;
            }

            return Task.CompletedTask;
        }

        public long GetPersistedValue(string key)
        {
            lock (_gate)
            {
                return _values.GetValueOrDefault(key, 0);
            }
        }
    }
}
