using System.Text;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Titles;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

/// <summary>
/// Vérifie que les endpoints /thumb/{w} (Phase 2) fonctionnent correctement :
/// - Fallback vers legacy poster_file quand le store est vide
/// - Serve depuis le store quand un thumb WebP existe
/// - Headers Cache-Control immutables
/// - 404 sans données
/// </summary>
public sealed class PostersControllerThumbTests
{
    // ── fallback legacy ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetReleasePosterThumb_NoStore_FallsBackToFlatFile()
    {
        using var ctx = new ThumbContext();
        var id = ctx.CreateRelease("poster.jpg");
        ctx.CreatePosterFile("poster.jpg"); // flat file exists, no store
        var controller = ctx.CreateController();

        var result = await controller.GetReleasePosterThumb(id, 500, CancellationToken.None);

        // Should serve the flat file (PhysicalFileResult) with immutable cache header
        Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("public, max-age=31536000, immutable",
            controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task GetReleasePosterThumb_NoRelease_Returns404()
    {
        using var ctx = new ThumbContext();
        var controller = ctx.CreateController();

        var result = await controller.GetReleasePosterThumb(99999, 500, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetReleasePosterThumb_NoFile_Returns404()
    {
        using var ctx = new ThumbContext();
        var id = ctx.CreateRelease(null); // no poster_file
        var controller = ctx.CreateController();

        var result = await controller.GetReleasePosterThumb(id, 500, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    // ── store thumb hit ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetReleasePosterThumb_WithStoreThumb_ServesWebP()
    {
        using var ctx = new ThumbContext();
        var id = ctx.CreateRelease("poster.jpg", storeDir: "tmdb-550");
        ctx.CreateStoreThumb("tmdb-550", 500); // pre-generated w500.webp
        var controller = ctx.CreateController();

        var result = await controller.GetReleasePosterThumb(id, 500, CancellationToken.None);

        var physical = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("image/webp", physical.ContentType);
        Assert.Equal("public, max-age=31536000, immutable",
            controller.Response.Headers.CacheControl.ToString());
        Assert.False(controller.Response.Headers.ContainsKey("X-Thumb-Fallback"));
    }

    [Fact]
    public async Task GetReleasePosterThumb_UnsupportedWidth_ClampsToNearest()
    {
        using var ctx = new ThumbContext();
        var id = ctx.CreateRelease("poster.jpg", storeDir: "tmdb-550");
        ctx.CreateStoreThumb("tmdb-550", 500); // only w500 available
        var controller = ctx.CreateController();

        // Request w=480 → nearest supported = 500
        var result = await controller.GetReleasePosterThumb(id, 480, CancellationToken.None);

        Assert.IsType<PhysicalFileResult>(result);
    }

    [Fact]
    public async Task GetReleasePosterThumb_MissingThumb_EnqueuesWarmup_AndServesOriginal()
    {
        using var ctx = new ThumbContext();
        var id = ctx.CreateRelease("poster.jpg", storeDir: "tmdb-550");
        ctx.CreateStoreOriginal("tmdb-550", ".jpg");
        var thumbQueue = new CaptureThumbQueue();
        var controller = ctx.CreateController(thumbQueue);

        var result = await controller.GetReleasePosterThumb(id, 500, CancellationToken.None);

        var physical = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("image/jpeg", physical.ContentType);
        Assert.True(thumbQueue.LastEnqueuedJob is not null);
        Assert.Equal("tmdb-550", thumbQueue.LastEnqueuedJob!.StoreDir);
        Assert.Equal(PosterThumbJobReason.MissingThumb, thumbQueue.LastEnqueuedJob.Reason);
        Assert.Contains(500, thumbQueue.LastEnqueuedJob.Widths ?? []);
        Assert.False(File.Exists(Path.Combine(ctx.StoreDir, "tmdb-550", "w500.webp")));
        Assert.Equal("public, max-age=5", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal("1", controller.Response.Headers["X-Thumb-Fallback"].ToString());
    }

    [Fact]
    public async Task GetReleasePosterThumb_MissingThumb_UsesConfiguredEnqueueTimeout()
    {
        using var ctx = new ThumbContext();
        var id = ctx.CreateRelease("poster.jpg", storeDir: "tmdb-550");
        ctx.CreateStoreOriginal("tmdb-550", ".jpg");
        var thumbQueue = new CaptureThumbQueue();
        var options = OptionsFactory.Create(new AppOptions
        {
            DataDir = ctx.DataDir,
            DbFileName = "feedarr.db",
            ThumbEnqueueTimeoutMs = 3210
        });
        var controller = ctx.CreateController(thumbQueue, options);

        _ = await controller.GetReleasePosterThumb(id, 500, CancellationToken.None);

        Assert.Equal(TimeSpan.FromMilliseconds(3210), thumbQueue.LastTimeout);
    }

    // ── entity thumb ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetEntityPosterThumb_NoStore_FallsBackToFlatFile()
    {
        using var ctx = new ThumbContext();
        var entityId = ctx.CreateEntity("entity-poster.jpg");
        ctx.CreatePosterFile("entity-poster.jpg");
        var controller = ctx.CreateController();

        var result = await controller.GetEntityPosterThumb(entityId, 500, CancellationToken.None);

        Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("public, max-age=31536000, immutable",
            controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task GetEntityPosterThumb_NoEntity_Returns404()
    {
        using var ctx = new ThumbContext();
        var controller = ctx.CreateController();

        var result = await controller.GetEntityPosterThumb(99999, 500, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private sealed class ThumbContext : IDisposable
    {
        private readonly TestWorkspace2 _workspace;
        private readonly TestWebHostEnvironment2 _env;

        public ThumbContext()
        {
            _workspace = new TestWorkspace2();
            _env = new TestWebHostEnvironment2(_workspace.RootDir);
            Options = OptionsFactory.Create(new AppOptions
            {
                DataDir = _workspace.DataDir,
                DbFileName = "feedarr.db"
            });

            Db = new Db(Options);
            new MigrationsRunner(Db, NullLogger<MigrationsRunner>.Instance).Run();

            var protection = new PassthroughProtectionService2();
            var settings = new SettingsRepository(Db, protection, NullLogger<SettingsRepository>.Instance);
            Releases = new ReleaseRepository(Db, new TitleParser(), new UnifiedCategoryResolver());
            MediaEntities = new MediaEntityRepository(Db);
            var activity = new ActivityRepository(Db, new BadgeSignal());
            var registry = new ExternalProviderRegistry();
            var instances = new ExternalProviderInstanceRepository(
                Db, settings, protection, registry,
                NullLogger<ExternalProviderInstanceRepository>.Instance);
            var activeResolver = new ActiveExternalProviderConfigResolver(
                instances, registry,
                NullLogger<ActiveExternalProviderConfigResolver>.Instance);

            PosterFetch = new PosterFetchService(
                Releases, activity,
                null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
                new PosterMatchCacheService(Db),
                Options, _env,
                new PosterMatchingOrchestrator(
                    new VideoMatchingStrategy(),
                    new GameMatchingStrategy(),
                    new AnimeMatchingStrategy(),
                    new AudioMatchingStrategy(),
                    new GenericMatchingStrategy()),
                activeResolver,
                NullLogger<PosterFetchService>.Instance);

            RetroLogs = new RetroFetchLogService(Options, _env);

            using var conn = Db.Open();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            conn.Execute(
                """
                INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
                VALUES('test', 1, 'http://localhost:9117/api', 'key', 'query', @ts, @ts);
                """,
                new { ts });
            SourceId = conn.ExecuteScalar<long>("SELECT id FROM sources LIMIT 1;");
        }

        public Db Db { get; }
        public ReleaseRepository Releases { get; }
        public MediaEntityRepository MediaEntities { get; }
        public PosterFetchService PosterFetch { get; }
        public RetroFetchLogService RetroLogs { get; }
        public Microsoft.Extensions.Options.IOptions<AppOptions> Options { get; }
        public long SourceId { get; }
        public string PostersDir => PosterFetch.PostersDirPath;
        public string StoreDir => PosterFetch.PosterStoreDirPath;
        public string DataDir => _workspace.DataDir;

        public Controllers.PostersController CreateController(IPosterThumbQueue? thumbQueue = null, Microsoft.Extensions.Options.IOptions<AppOptions>? optionsOverride = null)
        {
            var controller = new Controllers.PostersController(
                Releases, MediaEntities,
                new ActivityRepository(Db, new BadgeSignal()),
                PosterFetch,
                new NoOpQueue2(),
                new PosterFetchJobFactory(Releases),
                RetroLogs,
                null!, null!, null!, null!, null!, null!, null!, null!, null!,
                NullLogger<Controllers.PostersController>.Instance,
                new PosterThumbService(NullLogger<PosterThumbService>.Instance),
                thumbQueue,
                null,
                optionsOverride ?? Options);

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
            return controller;
        }

        public long CreateRelease(string? posterFile, string? storeDir = null)
        {
            using var conn = Db.Open();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return conn.ExecuteScalar<long>(
                """
                INSERT INTO releases(
                  source_id, guid, title, created_at_ts, published_at_ts,
                  title_clean, year, unified_category, media_type, poster_file, poster_store_dir
                )
                VALUES(
                  @sourceId, @guid, 'The Matrix', @ts, @ts,
                  'The Matrix', 1999, 'Film', 'movie', @posterFile, @storeDir
                );
                SELECT last_insert_rowid();
                """,
                new { sourceId = SourceId, guid = Guid.NewGuid().ToString("N"), ts, posterFile, storeDir });
        }

        public long CreateEntity(string? posterFile)
        {
            using var conn = Db.Open();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return conn.ExecuteScalar<long>(
                """
                INSERT INTO media_entities(
                  unified_category, title_clean, year, poster_file,
                  poster_updated_at_ts, created_at_ts, updated_at_ts
                )
                VALUES('Film', 'The Matrix', 1999, @posterFile, @ts, @ts, @ts);
                SELECT last_insert_rowid();
                """,
                new { posterFile, ts });
        }

        public void CreatePosterFile(string fileName)
        {
            Directory.CreateDirectory(PostersDir);
            File.WriteAllBytes(Path.Combine(PostersDir, fileName), Encoding.UTF8.GetBytes("poster-bytes"));
        }

        public void CreateStoreThumb(string storeDir, int width)
        {
            var dir = Path.Combine(StoreDir, storeDir);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"w{width}.webp"), Encoding.UTF8.GetBytes("webp-thumb-bytes"));
        }

        public void CreateStoreOriginal(string storeDir, string ext)
        {
            var dir = Path.Combine(StoreDir, storeDir);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"original{ext}"), Encoding.UTF8.GetBytes("original-poster-bytes"));
        }

        public void Dispose() => _workspace.Dispose();
    }

    private sealed class NoOpQueue2 : IPosterFetchQueue
    {
        public ValueTask<PosterFetchEnqueueResult> EnqueueAsync(PosterFetchJob job, CancellationToken ct, TimeSpan timeout)
            => ValueTask.FromResult(new PosterFetchEnqueueResult(PosterFetchEnqueueStatus.Enqueued));
        public ValueTask<PosterFetchEnqueueBatchResult> EnqueueManyAsync(IReadOnlyList<PosterFetchJob> jobs, CancellationToken ct, TimeSpan timeout)
            => ValueTask.FromResult(new PosterFetchEnqueueBatchResult(jobs.Count, 0, 0, 0));
        public ValueTask<PosterFetchJob> DequeueAsync(CancellationToken ct) => ValueTask.FromCanceled<PosterFetchJob>(ct);
        public void RecordRetry() { }
        public PosterFetchJob? Complete(PosterFetchJob job, PosterFetchProcessResult result) => null;
        public int ClearPending() => 0;
        public int Count => 0;
        public PosterFetchQueueSnapshot GetSnapshot()
            => new(0, 0, false, null, null, null, null, 0, 0, 0, 0, 0, 0, 0);
    }

    private sealed class CaptureThumbQueue : IPosterThumbQueue
    {
        public PosterThumbJob? LastEnqueuedJob { get; private set; }
        public TimeSpan LastTimeout { get; private set; }

        public ValueTask<PosterThumbEnqueueResult> EnqueueAsync(PosterThumbJob job, CancellationToken ct, TimeSpan timeout)
        {
            LastEnqueuedJob = job;
            LastTimeout = timeout;
            return ValueTask.FromResult(new PosterThumbEnqueueResult(PosterThumbEnqueueStatus.Enqueued));
        }

        public ValueTask<PosterThumbJob> DequeueAsync(CancellationToken ct) => ValueTask.FromCanceled<PosterThumbJob>(ct);

        public PosterThumbJob? Complete(PosterThumbJob job) => null;

        public int Count => LastEnqueuedJob is null ? 0 : 1;
    }

    private sealed class PassthroughProtectionService2 : IApiKeyProtectionService
    {
        public string Protect(string plainText) => plainText;
        public string Unprotect(string protectedText) => protectedText;
        public bool TryUnprotect(string protectedText, out string? plainText) { plainText = protectedText; return true; }
        public bool IsProtected(string value) => false;
    }

    private sealed class TestWebHostEnvironment2 : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public TestWebHostEnvironment2(string rootDir)
        {
            ApplicationName = "Feedarr.Api.Tests";
            EnvironmentName = "Test";
            ContentRootPath = rootDir;
            ContentRootFileProvider = new NullFileProvider();
            WebRootPath = rootDir;
            WebRootFileProvider = new NullFileProvider();
        }
        public string ApplicationName { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public string EnvironmentName { get; set; }
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }

    private sealed class TestWorkspace2 : IDisposable
    {
        public TestWorkspace2()
        {
            RootDir = Path.Combine(Path.GetTempPath(), "feedarr-thumb-tests", Guid.NewGuid().ToString("N"));
            DataDir = Path.Combine(RootDir, "data");
            Directory.CreateDirectory(DataDir);
        }
        public string RootDir { get; }
        public string DataDir { get; }
        public void Dispose()
        {
            try { if (Directory.Exists(RootDir)) Directory.Delete(RootDir, true); } catch { }
        }
    }
}
