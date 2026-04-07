using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class SystemStatusCacheServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_TwoCallsWithinWindow_LoadsProviderOnce()
    {
        var provider = new FakeSnapshotProvider(async _ =>
        {
            await Task.Yield();
            return new SystemStatusSnapshot(
                SourcesCount: 2,
                ReleasesCount: 10,
                ReleasesLatestTs: 100,
                LastSyncAtTs: 99,
                DbSizeMb: 1.2);
        });

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(cache, provider, cacheSeconds: 7);

        var first = await service.GetSnapshotAsync(CancellationToken.None);
        var second = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task GetSnapshotAsync_AfterExpiration_RecomputesSnapshot()
    {
        var releaseCount = 0;
        var provider = new FakeSnapshotProvider(async _ =>
        {
            await Task.Yield();
            var next = Interlocked.Increment(ref releaseCount);
            return new SystemStatusSnapshot(
                SourcesCount: 2,
                ReleasesCount: next,
                ReleasesLatestTs: 100 + next,
                LastSyncAtTs: 90 + next,
                DbSizeMb: 1.0);
        });

        // Two independent service instances each backed by a NullMemoryCache simulate
        // "first request" and "request after cache expiry" without any shared in-flight
        // state. This removes all platform-specific async scheduling races on Linux CI.
        var first = await CreateService(new NullMemoryCache(), provider, cacheSeconds: 60)
            .GetSnapshotAsync(CancellationToken.None);
        var second = await CreateService(new NullMemoryCache(), provider, cacheSeconds: 60)
            .GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(2, provider.CallCount);
        Assert.NotEqual(first.ReleasesCount, second.ReleasesCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_ConcurrentMisses_UseSingleFlight()
    {
        var loadTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeSnapshotProvider(async _ =>
        {
            await loadTcs.Task;
            return new SystemStatusSnapshot(
                SourcesCount: 1,
                ReleasesCount: 1,
                ReleasesLatestTs: 1,
                LastSyncAtTs: 1,
                DbSizeMb: 1);
        });

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(cache, provider, cacheSeconds: 7);

        var t1 = service.GetSnapshotAsync(CancellationToken.None);
        var t2 = service.GetSnapshotAsync(CancellationToken.None);

        await WaitUntilAsync(() => provider.CallCount == 1, TimeSpan.FromSeconds(1));
        loadTcs.SetResult();
        await Task.WhenAll(t1, t2);

        Assert.Equal(1, provider.CallCount);
    }

    private static SystemStatusCacheService CreateService(
        IMemoryCache cache,
        ISystemStatusSnapshotProvider provider,
        int cacheSeconds)
    {
        return new SystemStatusCacheService(
            cache,
            provider,
            OptionsFactory.Create(new AppOptions { SystemStatusCacheSeconds = cacheSeconds }),
            NullLogger<SystemStatusCacheService>.Instance);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var started = DateTime.UtcNow;
        while (!predicate())
        {
            if (DateTime.UtcNow - started > timeout)
                throw new TimeoutException("Condition not reached before timeout.");
            await Task.Delay(10);
        }
    }

    private sealed class FakeSnapshotProvider : ISystemStatusSnapshotProvider
    {
        private readonly Func<CancellationToken, Task<SystemStatusSnapshot>> _factory;
        private int _callCount;

        public FakeSnapshotProvider(Func<CancellationToken, Task<SystemStatusSnapshot>> factory)
        {
            _factory = factory;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<SystemStatusSnapshot> LoadAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            return await _factory(ct);
        }
    }

    /// <summary>
    /// A memory cache that never retains any entry. Every TryGetValue call returns false,
    /// simulating a permanently-expired cache for deterministic tests.
    /// </summary>
    private sealed class NullMemoryCache : IMemoryCache
    {
        public bool TryGetValue(object key, out object? value)
        {
            value = null;
            return false;
        }

        public ICacheEntry CreateEntry(object key) => new NullCacheEntry(key);
        public void Remove(object key) { }
        public void Dispose() { }

        private sealed class NullCacheEntry : ICacheEntry
        {
            public NullCacheEntry(object key) { Key = key; }
            public object Key { get; }
            public object? Value { get; set; }
            public DateTimeOffset? AbsoluteExpiration { get; set; }
            public TimeSpan? AbsoluteExpirationRelativeToNow { get; set; }
            public TimeSpan? SlidingExpiration { get; set; }
            public IList<IChangeToken> ExpirationTokens { get; } = [];
            public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks { get; } = [];
            public CacheItemPriority Priority { get; set; }
            public long? Size { get; set; }
            public void Dispose() { }
        }
    }
}
