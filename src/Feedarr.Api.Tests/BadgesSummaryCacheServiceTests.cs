using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class BadgesSummaryCacheServiceTests
{
    [Fact]
    public async Task GetBaseSummaryAsync_TwoCallsWithinWindow_LoadsProviderOnce()
    {
        var provider = new FakeBadgesBaseSummaryProvider(async _ =>
        {
            await Task.Yield();
            return SampleSummary();
        });

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(cache, provider, cacheSeconds: 3);

        var first = await service.GetBaseSummaryAsync(CancellationToken.None);
        var second = await service.GetBaseSummaryAsync(CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task GetBaseSummaryAsync_AfterExpiration_RecomputesSummary()
    {
        var sequence = 0;
        var provider = new FakeBadgesBaseSummaryProvider(async _ =>
        {
            await Task.Yield();
            var next = Interlocked.Increment(ref sequence);
            return SampleSummary() with { ReleasesCount = 10 + next };
        });

        // Two independent service instances each backed by a NullMemoryCache simulate
        // "first request" and "request after cache expiry" without any shared in-flight
        // state. This removes all platform-specific async scheduling races on Linux CI.
        var first = await CreateService(new NullMemoryCache(), provider, cacheSeconds: 60)
            .GetBaseSummaryAsync(CancellationToken.None);
        var second = await CreateService(new NullMemoryCache(), provider, cacheSeconds: 60)
            .GetBaseSummaryAsync(CancellationToken.None);

        Assert.Equal(2, provider.CallCount);
        Assert.NotEqual(first.ReleasesCount, second.ReleasesCount);
    }

    [Fact]
    public async Task GetBaseSummaryAsync_ConcurrentMisses_UseSingleFlight()
    {
        var loadTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeBadgesBaseSummaryProvider(async _ =>
        {
            await loadTcs.Task;
            return SampleSummary();
        });

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(cache, provider, cacheSeconds: 3);

        var t1 = service.GetBaseSummaryAsync(CancellationToken.None);
        var t2 = service.GetBaseSummaryAsync(CancellationToken.None);

        await WaitUntilAsync(() => provider.CallCount == 1, TimeSpan.FromSeconds(1));
        loadTcs.SetResult();
        await Task.WhenAll(t1, t2);

        Assert.Equal(1, provider.CallCount);
    }

    private static BadgesSummaryCacheService CreateService(
        IMemoryCache cache,
        IBadgesBaseSummaryProvider provider,
        int cacheSeconds)
    {
        return new BadgesSummaryCacheService(
            cache,
            provider,
            OptionsFactory.Create(new AppOptions { BadgesSummaryCacheSeconds = cacheSeconds }),
            NullLogger<BadgesSummaryCacheService>.Instance);
    }

    private static BadgesBaseSummary SampleSummary()
    {
        return new BadgesBaseSummary(
            LastActivityTs: 1000,
            SourcesCount: 2,
            ReleasesCount: 10,
            ReleasesLatestTs: 995,
            IncludeInfo: true,
            IncludeWarn: true,
            IncludeError: true,
            MissingExternalCount: 1,
            HasAdvancedMaintenanceEnabled: false,
            IsSyncRunning: false,
            SchedulerBusy: false,
            UpdatesBadge: false);
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

    private sealed class FakeBadgesBaseSummaryProvider : IBadgesBaseSummaryProvider
    {
        private readonly Func<CancellationToken, Task<BadgesBaseSummary>> _factory;
        private int _callCount;

        public FakeBadgesBaseSummaryProvider(Func<CancellationToken, Task<BadgesBaseSummary>> factory)
        {
            _factory = factory;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<BadgesBaseSummary> LoadAsync(CancellationToken ct)
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
