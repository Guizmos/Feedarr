using System.Text;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Controllers;
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
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class MaintenancePosterRepairTests
{
    [Fact]
    public void RepairMissingPosters_DryRun_DoesNotMutateDatabase()
    {
        using var context = new MaintenancePosterRepairContext();
        var missingReleaseId = context.CreateRelease("missing.jpg");
        var existingReleaseId = context.CreateRelease("existing.jpg");
        context.CreatePosterMatch("missing.jpg");
        context.CreatePosterMatch("existing.jpg");
        context.CreatePosterFile("existing.jpg");
        var controller = context.CreateController();

        var result = controller.RepairMissingPosters(dryRun: true, olderThanDays: null, limit: 10, offset: 0);

        var ok = Assert.IsType<OkObjectResult>(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        Assert.True(doc.RootElement.GetProperty("dryRun").GetBoolean());
        Assert.Equal(2, doc.RootElement.GetProperty("checked").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("missing").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("invalidated").GetInt32());
        Assert.Equal("missing.jpg", context.GetReleasePosterFile(missingReleaseId));
        Assert.Equal("existing.jpg", context.GetReleasePosterFile(existingReleaseId));
        Assert.Equal(1, context.GetPosterMatchCount("missing.jpg"));
        Assert.Equal(1, context.GetPosterMatchCount("existing.jpg"));
    }

    [Fact]
    public void RepairMissingPosters_Execute_InvalidatesOnlyMissingPosters()
    {
        using var context = new MaintenancePosterRepairContext();
        var missingReleaseId = context.CreateRelease("missing.jpg");
        var existingReleaseId = context.CreateRelease("existing.jpg");
        context.CreatePosterMatch("missing.jpg");
        context.CreatePosterMatch("existing.jpg");
        context.CreatePosterFile("existing.jpg");
        var controller = context.CreateController();

        var result = controller.RepairMissingPosters(dryRun: false, olderThanDays: null, limit: 10, offset: 0);

        var ok = Assert.IsType<OkObjectResult>(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        Assert.False(doc.RootElement.GetProperty("dryRun").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("missing").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("invalidated").GetInt32());
        Assert.Null(context.GetReleasePosterFile(missingReleaseId));
        Assert.Equal("existing.jpg", context.GetReleasePosterFile(existingReleaseId));
        Assert.Equal(0, context.GetPosterMatchCount("missing.jpg"));
        Assert.Equal(1, context.GetPosterMatchCount("existing.jpg"));
    }

    private sealed class MaintenancePosterRepairContext : IDisposable
    {
        private readonly TestWorkspace _workspace;
        private readonly TestWebHostEnvironment _environment;

        public MaintenancePosterRepairContext()
        {
            _workspace = new TestWorkspace();
            _environment = new TestWebHostEnvironment(_workspace.RootDir);
            Options = OptionsFactory.Create(new AppOptions
            {
                DataDir = _workspace.DataDir,
                DbFileName = "feedarr.db"
            });

            Db = new Db(Options);
            new MigrationsRunner(Db, NullLogger<MigrationsRunner>.Instance).Run();

            var protection = new PassthroughProtectionService();
            Settings = new SettingsRepository(Db, protection, NullLogger<SettingsRepository>.Instance);
            Releases = new ReleaseRepository(Db, new TitleParser(), new UnifiedCategoryResolver());
            Activity = new ActivityRepository(Db, new BadgeSignal());
            LockService = new MaintenanceLockService();

            var registry = new ExternalProviderRegistry();
            var instances = new ExternalProviderInstanceRepository(
                Db,
                Settings,
                protection,
                registry,
                NullLogger<ExternalProviderInstanceRepository>.Instance);
            var resolver = new ActiveExternalProviderConfigResolver(
                instances,
                registry,
                NullLogger<ActiveExternalProviderConfigResolver>.Instance);

            PosterFetch = new PosterFetchService(
                Releases,
                Activity,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                new PosterMatchCacheService(Db),
                Options,
                _environment,
                new PosterMatchingOrchestrator(
                    new VideoMatchingStrategy(),
                    new GameMatchingStrategy(),
                    new AnimeMatchingStrategy(),
                    new AudioMatchingStrategy(),
                    new GenericMatchingStrategy()),
                resolver,
                NullLogger<PosterFetchService>.Instance);

            using var conn = Db.Open();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SourceId = conn.ExecuteScalar<long>(
                """
                INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
                VALUES('test', 1, 'http://localhost:9117/api', 'key', 'query', @ts, @ts);
                SELECT last_insert_rowid();
                """,
                new { ts });
        }

        public Db Db { get; }
        public ReleaseRepository Releases { get; }
        public ActivityRepository Activity { get; }
        public SettingsRepository Settings { get; }
        public PosterFetchService PosterFetch { get; }
        public MaintenanceLockService LockService { get; }
        public Microsoft.Extensions.Options.IOptions<AppOptions> Options { get; }
        public long SourceId { get; }

        public MaintenanceController CreateController()
        {
            return new MaintenanceController(
                Db,
                Releases,
                Activity,
                Settings,
                PosterFetch,
                new RetroFetchLogService(Options, _environment),
                null!,
                null!,
                null!,
                null!,
                Options,
                _environment,
                LockService,
                NullLogger<MaintenanceController>.Instance);
        }

        public long CreateRelease(string posterFile)
        {
            using var conn = Db.Open();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return conn.ExecuteScalar<long>(
                """
                INSERT INTO releases(
                  source_id, guid, title, created_at_ts, published_at_ts, unified_category, media_type, poster_file, poster_updated_at_ts
                )
                VALUES(
                  @sourceId, @guid, 'The Matrix', @ts, @ts, 'Film', 'movie', @posterFile, @ts
                );
                SELECT last_insert_rowid();
                """,
                new
                {
                    sourceId = SourceId,
                    guid = Guid.NewGuid().ToString("N"),
                    ts,
                    posterFile
                });
        }

        public void CreatePosterFile(string fileName)
        {
            Directory.CreateDirectory(PosterFetch.PostersDirPath);
            File.WriteAllBytes(Path.Combine(PosterFetch.PostersDirPath, fileName), Encoding.UTF8.GetBytes("poster"));
        }

        public void CreatePosterMatch(string posterFile)
        {
            using var conn = Db.Open();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            conn.Execute(
                """
                INSERT INTO poster_matches(
                  fingerprint,
                  media_type,
                  normalized_title,
                  year,
                  ids_json,
                  confidence,
                  match_source,
                  poster_file,
                  created_ts,
                  last_seen_ts
                )
                VALUES(
                  @fingerprint,
                  'movie',
                  'the matrix',
                  1999,
                  '{}',
                  0.99,
                  'test',
                  @posterFile,
                  @ts,
                  @ts
                );
                """,
                new
                {
                    fingerprint = Guid.NewGuid().ToString("N"),
                    posterFile,
                    ts
                });
        }

        public string? GetReleasePosterFile(long releaseId)
        {
            using var conn = Db.Open();
            return conn.QuerySingleOrDefault<string>(
                "SELECT poster_file FROM releases WHERE id = @id;",
                new { id = releaseId });
        }

        public int GetPosterMatchCount(string posterFile)
        {
            using var conn = Db.Open();
            return conn.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM poster_matches WHERE poster_file = @posterFile;",
                new { posterFile });
        }

        public void Dispose()
        {
            _workspace.Dispose();
        }
    }

    private sealed class PassthroughProtectionService : IApiKeyProtectionService
    {
        public string Protect(string plainText) => plainText;
        public string Unprotect(string protectedText) => protectedText;
        public bool TryUnprotect(string protectedText, out string? plainText)
        {
            plainText = protectedText;
            return true;
        }

        public bool IsProtected(string value) => false;
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
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
            RootDir = Path.Combine(Path.GetTempPath(), "feedarr-tests", Guid.NewGuid().ToString("N"));
            DataDir = Path.Combine(RootDir, "data");
            Directory.CreateDirectory(DataDir);
        }

        public string RootDir { get; }
        public string DataDir { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootDir))
                    Directory.Delete(RootDir, true);
            }
            catch
            {
            }
        }
    }
}
