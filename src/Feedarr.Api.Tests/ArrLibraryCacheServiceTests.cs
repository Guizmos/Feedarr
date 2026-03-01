using System.Net;
using System.Text;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Arr;
using Feedarr.Api.Services.Security;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class ArrLibraryCacheServiceTests
{
    // ─── Setup helpers ──────────────────────────────────────────────────────

    private static Db CreateDb(string dataDir)
    {
        var db = new Db(OptionsFactory.Create(new AppOptions
        {
            DataDir = dataDir,
            DbFileName = "feedarr.db",
        }));
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();
        return db;
    }

    private static (ArrLibraryCacheService svc, TestWorkspace ws) BuildService(
        HttpMessageHandler sonarrHandler,
        int maxTitleEntries = 5_000)
    {
        var ws = new TestWorkspace();
        var db = CreateDb(ws.DataDir);
        var repo = new ArrApplicationRepository(db, new PassthroughProtectionService());

        repo.Create(
            type: "sonarr",
            name: "Test Sonarr",
            baseUrl: "http://localhost:8989",
            apiKeyEncrypted: "secret",
            rootFolderPath: null,
            qualityProfileId: null,
            tags: null,
            seriesType: null,
            seasonFolder: true,
            monitorMode: null,
            searchMissing: true,
            searchCutoff: false,
            minimumAvailability: null,
            searchForMovie: true);

        var sonarr = new SonarrClient(new HttpClient(sonarrHandler));
        var radarr = new RadarrClient(new HttpClient(new EmptyJsonHandler()));

        var opts = OptionsFactory.Create(new ArrLibraryCacheOptions
        {
            MaxTitleEntries = maxTitleEntries,
            SlidingExpirationMinutes = 30,
            AbsoluteExpirationHours = 6,
        });

        var svc = new ArrLibraryCacheService(
            repo, sonarr, radarr,
            NullLogger<ArrLibraryCacheService>.Instance,
            opts);

        return (svc, ws);
    }

    // ─── Tests ──────────────────────────────────────────────────────────────

    /// <summary>
    /// After one HTTP refresh, two consecutive title lookups should both hit the
    /// MemoryCache. No additional HTTP call must be made. Metrics reflect 2 hits, 0 misses.
    /// </summary>
    [Fact]
    public async Task TitleCacheHit_ReturnsEntryAndCountsHits()
    {
        var handler = new CountingHandler("""
            [
              { "id": 101, "tvdbId": 9001, "title": "Breaking Bad",
                "titleSlug": "breaking-bad", "alternateTitles": [] }
            ]
            """);

        var (svc, ws) = BuildService(handler);
        using var _ws = ws;
        using var _svc = svc;

        await svc.RefreshSonarrCacheAsync(CancellationToken.None);
        Assert.Equal(1, handler.CallCount);

        var first  = svc.CheckSonarrExistsByTitle("Breaking Bad");
        var second = svc.CheckSonarrExistsByTitle("Breaking Bad");

        Assert.True(first.exists);
        Assert.True(second.exists);
        Assert.Equal(1, handler.CallCount); // still served from cache

        var m = svc.GetMetrics();
        Assert.Equal(2, m.SonarrTitleHits);
        Assert.Equal(0, m.SonarrTitleMisses);
    }

    /// <summary>
    /// After refreshing the cache, calling ClearCache() compacts all title entries
    /// (Compact(1.0)), which fires PostEvictionCallbacks. The eviction metric must
    /// reflect at least one eviction per cached title key, and titles must not be
    /// found after the clear.
    /// </summary>
    [Fact]
    public async Task ClearCache_EvictsAllTitleEntries_AndFiresCallbacks()
    {
        var handler = new CountingHandler("""
            [
              { "id": 1, "tvdbId": 1001, "title": "Show Alpha",
                "titleSlug": "show-alpha", "alternateTitles": [] },
              { "id": 2, "tvdbId": 1002, "title": "Show Beta",
                "titleSlug": "show-beta", "alternateTitles": [] }
            ]
            """);

        var (svc, ws) = BuildService(handler);
        using var _ws = ws;
        using var _svc = svc;

        await svc.RefreshSonarrCacheAsync(CancellationToken.None);

        Assert.True(svc.CheckSonarrExistsByTitle("Show Alpha").exists);
        Assert.True(svc.CheckSonarrExistsByTitle("Show Beta").exists);

        // ClearCache calls Compact(1.0) which evicts all title entries synchronously
        svc.ClearCache();

        Assert.False(svc.CheckSonarrExistsByTitle("Show Alpha").exists);
        Assert.False(svc.CheckSonarrExistsByTitle("Show Beta").exists);

        // PostEvictionCallback fires on a MemoryCache background thread; spin-wait with a generous timeout
        var evicted = SpinWait.SpinUntil(() => svc.GetMetrics().Evictions >= 2, TimeSpan.FromSeconds(5));
        Assert.True(evicted, $"Expected Evictions >= 2 but got {svc.GetMetrics().Evictions}");
    }

    /// <summary>
    /// When 10 concurrent tasks call EnsureSonarrCacheFreshAsync on an empty cache,
    /// the SemaphoreSlim anti-stampede must ensure only one HTTP request is made.
    /// The remaining 9 tasks must observe the fresh cache via the double-check inside
    /// the lock and return without triggering another refresh.
    /// </summary>
    [Fact]
    public async Task ConcurrentRefresh_DeduplicatesToSingleHttpFetch()
    {
        var handler = new CountingHandler("""
            [
              { "id": 101, "tvdbId": 9001, "title": "Breaking Bad",
                "titleSlug": "breaking-bad", "alternateTitles": [] }
            ]
            """);

        var (svc, ws) = BuildService(handler);
        using var _ws = ws;
        using var _svc = svc;

        // 10 concurrent callers, all seeing a stale/empty cache
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => svc.EnsureSonarrCacheFreshAsync(CancellationToken.None))
            .ToArray();

        await Task.WhenAll(tasks);

        // Exactly one HTTP request should have been issued
        Assert.Equal(1, handler.CallCount);

        // Cache is populated after the single refresh
        Assert.True(svc.CheckSonarrExistsByTitle("Breaking Bad").exists);
    }

    /// <summary>
    /// After a ClearCache(), the ID cache is empty (stale). A subsequent
    /// RefreshSonarrCacheAsync must repopulate both the ID and title caches.
    /// Two HTTP calls in total: one for the initial load, one after the clear.
    /// </summary>
    [Fact]
    public async Task StalenessAfterClear_RefreshRestoresEntries()
    {
        var handler = new CountingHandler("""
            [
              { "id": 55, "tvdbId": 5555, "title": "The Expanse",
                "titleSlug": "the-expanse", "alternateTitles": [] }
            ]
            """);

        var (svc, ws) = BuildService(handler);
        using var _ws = ws;
        using var _svc = svc;

        // Initial populate
        await svc.RefreshSonarrCacheAsync(CancellationToken.None);
        Assert.True(svc.CheckSonarrExistsByTitle("The Expanse").exists);

        // Clear makes cache stale (ID cache emptied, title cache compacted)
        svc.ClearCache();
        Assert.False(svc.CheckSonarrExistsByTitle("The Expanse").exists);

        // Re-refresh re-populates the cache
        await svc.RefreshSonarrCacheAsync(CancellationToken.None);
        Assert.True(svc.CheckSonarrExistsByTitle("The Expanse").exists);

        Assert.Equal(2, handler.CallCount);
    }

    // ─── Private helpers ────────────────────────────────────────────────────

    /// <summary>Returns the configured JSON for every request and counts calls.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly string _json;
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public CountingHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>Returns an empty JSON array for every request (used for Radarr).</summary>
    private sealed class EmptyJsonHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>Identity protection service for tests: stores keys in plaintext.</summary>
    private sealed class PassthroughProtectionService : IApiKeyProtectionService
    {
        public string Protect(string plainText) => plainText;
        public string Unprotect(string protectedText) => protectedText;

        public bool TryUnprotect(string protectedText, out string plainText)
        {
            plainText = protectedText;
            return true;
        }

        public bool IsProtected(string value) => false;
    }

    private sealed class TestWorkspace : IDisposable
    {
        private readonly string _rootDir;

        public string DataDir { get; }

        public TestWorkspace()
        {
            _rootDir = Path.Combine(Path.GetTempPath(), "feedarr-tests", Guid.NewGuid().ToString("N"));
            DataDir = Path.Combine(_rootDir, "data");
            Directory.CreateDirectory(DataDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_rootDir))
                    Directory.Delete(_rootDir, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
