using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
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
/// Vérifie que les endpoints de posters émettent les bons headers Cache-Control
/// après le patch Phase 1 (perf: cache navigateur agressif).
/// </summary>
public sealed class PostersControllerCacheHeadersTests
{
    [Fact]
    public async Task SessionImagesRevalidateAndInvalidateEvenWithSameTimestampAndLength()
    {
        using var ctx = new CacheHeaderContext();
        var id = ctx.CreateRelease("conditional.webp"); ctx.CreatePosterFile("conditional.webp");
        var path = Path.Combine(ctx.PostersDir, "conditional.webp");
        var beforeTime = File.GetLastWriteTimeUtc(path);
        var controller = ctx.CreateController();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers();
        await using var app = builder.Build();
        app.Run(async http => {
                WebSessionSecurity.DisableCaching(http);
                if (http.Request.Path == "/api/settings/ui") { await http.Response.WriteAsync("sensitive"); return; }
                controller.ControllerContext = new ControllerContext { HttpContext = http };
                await controller.GetPoster(id).ExecuteResultAsync(controller.ControllerContext);
            });
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var first = await client.GetAsync("/api/posters/release/1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(first.Headers.CacheControl!.Private);
        Assert.True(first.Headers.CacheControl.NoCache);
        Assert.False(first.Headers.CacheControl.NoStore);
        Assert.NotNull(first.Headers.ETag);
        using var unchanged = new HttpRequestMessage(HttpMethod.Get, "/api/posters/release/1");
        unchanged.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        var second = await client.SendAsync(unchanged);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
        var bytes = await File.ReadAllBytesAsync(path); bytes[0] ^= 1;
        await File.WriteAllBytesAsync(path, bytes); File.SetLastWriteTimeUtc(path, beforeTime);
        using var changed = new HttpRequestMessage(HttpMethod.Get, "/api/posters/release/1");
        changed.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        var replacement = await client.SendAsync(changed);
        Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);
        Assert.NotEqual(first.Headers.ETag, replacement.Headers.ETag);
        Assert.Equal(bytes, await replacement.Content.ReadAsByteArrayAsync());
        var sensitive = await client.GetAsync("/api/settings/ui");
        Assert.Equal("no-store", sensitive.Headers.CacheControl!.ToString());
    }

    // ── release / entity : immutable long-lived ─────────────────────────────

    [Fact]
    public void GetPoster_SetsImmutableCacheControl()
    {
        using var ctx = new CacheHeaderContext();
        var id = ctx.CreateRelease("poster.jpg");
        ctx.CreatePosterFile("poster.jpg");
        var controller = ctx.CreateController();

        controller.GetPoster(id);

        Assert.Equal("private, no-cache",
            controller.Response.Headers.CacheControl.ToString());
        Assert.Equal("Accept-Encoding",
            controller.Response.Headers.Vary.ToString());
    }

    [Fact]
    public void GetEntityPoster_SetsImmutableCacheControl()
    {
        using var ctx = new CacheHeaderContext();
        var entityId = ctx.CreateEntity("entity-poster.jpg");
        ctx.CreatePosterFile("entity-poster.jpg");
        var controller = ctx.CreateController();

        controller.GetEntityPoster(entityId);

        Assert.Equal("private, no-cache",
            controller.Response.Headers.CacheControl.ToString());
        Assert.Equal("Accept-Encoding",
            controller.Response.Headers.Vary.ToString());
    }

    [Fact]
    public void GetPoster_NotFound_DoesNotSetCacheControl()
    {
        using var ctx = new CacheHeaderContext();
        // Release inexistante
        var controller = ctx.CreateController();

        controller.GetPoster(99999);

        Assert.False(controller.Response.Headers.ContainsKey("Cache-Control"));
    }

    [Fact]
    public void GetPoster_MissingFile_DoesNotSetCacheControl()
    {
        using var ctx = new CacheHeaderContext();
        var id = ctx.CreateRelease("ghost.jpg"); // fichier non créé sur disque
        var controller = ctx.CreateController();

        controller.GetPoster(id);

        Assert.False(controller.Response.Headers.ContainsKey("Cache-Control"));
    }

    // ── banner : court TTL sans immutable ───────────────────────────────────

    [Fact]
    public async Task GetBanner_CacheHit_SetsShortCacheControl()
    {
        using var ctx = new CacheHeaderContext();
        var id = ctx.CreateRelease("poster.jpg");
        // Crée le fichier banner pré-caché (banner-{id}.jpg dans le dossier posters)
        ctx.CreatePosterFile($"banner-{id}.jpg");
        var controller = ctx.CreateController();

        await controller.GetBanner(id, CancellationToken.None);

        Assert.Equal("private, no-cache",
            controller.Response.Headers.CacheControl.ToString());
        Assert.Equal("Accept-Encoding",
            controller.Response.Headers.Vary.ToString());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed class CacheHeaderContext : IDisposable
    {
        private readonly TestWorkspace _workspace;
        private readonly TestWebHostEnvironment _env;
        private readonly ExternalProviderRegistry _registry;
        private readonly ExternalProviderInstanceRepository _externalInstances;

        public CacheHeaderContext()
        {
            _workspace = new TestWorkspace();
            _env = new TestWebHostEnvironment(_workspace.RootDir);
            Options = OptionsFactory.Create(new AppOptions
            {
                DataDir = _workspace.DataDir,
                DbFileName = "feedarr.db"
            });

            Db = new Db(Options);
            new MigrationsRunner(Db, NullLogger<MigrationsRunner>.Instance).Run();

            var protection = new PassthroughProtectionService();
            var settings = new SettingsRepository(Db, protection, NullLogger<SettingsRepository>.Instance);
            Releases = new ReleaseRepository(Db, new TitleParser(), new UnifiedCategoryResolver());
            MediaEntities = new MediaEntityRepository(Db);
            var activity = new ActivityRepository(Db, new BadgeSignal());
            var stats = new ProviderStatsService(new StatsRepository(Db, new MemoryCache(new MemoryCacheOptions())));
            _registry = new ExternalProviderRegistry();
            _externalInstances = new ExternalProviderInstanceRepository(
                Db, settings, protection, _registry,
                NullLogger<ExternalProviderInstanceRepository>.Instance);

            var activeResolver = new ActiveExternalProviderConfigResolver(
                _externalInstances, _registry,
                NullLogger<ActiveExternalProviderConfigResolver>.Instance);

            PosterFetch = new PosterFetchService(
                Releases, activity,
                null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
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

        public Controllers.PostersController CreateController()
        {
            var controller = new Controllers.PostersController(
                Releases, MediaEntities,
                new ActivityRepository(Db, new BadgeSignal()),
                PosterFetch,
                new NoOpQueue(),
                new PosterFetchJobFactory(Releases),
                RetroLogs,
                null!, null!, null!, null!, null!, null!, null!, null!, null!,
                NullLogger<Controllers.PostersController>.Instance);

            // Fournir un HttpContext réel pour que Response.Headers soit accessible.
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
            return controller;
        }

        public long CreateRelease(string? posterFile = null)
        {
            using var conn = Db.Open();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return conn.ExecuteScalar<long>(
                """
                INSERT INTO releases(
                  source_id, guid, title, created_at_ts, published_at_ts,
                  title_clean, year, unified_category, media_type, poster_file
                )
                VALUES(
                  @sourceId, @guid, 'The Matrix', @ts, @ts,
                  'The Matrix', 1999, 'Film', 'movie', @posterFile
                );
                SELECT last_insert_rowid();
                """,
                new { sourceId = SourceId, guid = Guid.NewGuid().ToString("N"), ts, posterFile });
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

        public void Dispose() => _workspace.Dispose();
    }

    private sealed class NoOpQueue : IPosterFetchQueue
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

    private sealed class PassthroughProtectionService : IApiKeyProtectionService
    {
        public string Protect(string plainText) => plainText;
        public string Unprotect(string protectedText) => protectedText;
        public bool TryUnprotect(string protectedText, out string? plainText) { plainText = protectedText; return true; }
        public bool IsProtected(string value) => false;
    }

    private sealed class TestWebHostEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public TestWebHostEnvironment(string rootDir)
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

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            RootDir = Path.Combine(Path.GetTempPath(), "feedarr-cache-tests", Guid.NewGuid().ToString("N"));
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
