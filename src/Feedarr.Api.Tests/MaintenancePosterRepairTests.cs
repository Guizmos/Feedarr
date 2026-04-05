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
using Microsoft.Extensions.Logging;
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

    [Fact]
    public void RepairMissingPosters_Execute_LogsCompletionInformation()
    {
        using var context = new MaintenancePosterRepairContext();
        context.CreateRelease("missing.jpg");
        context.CreatePosterMatch("missing.jpg");
        var logger = new ListLogger<MaintenanceController>();
        var controller = context.CreateController(logger);

        var result = controller.RepairMissingPosters(dryRun: false, olderThanDays: null, limit: 10, offset: 0);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(logger.Contains(LogLevel.Information, "RepairMissingPosters completed"));
    }

    [Fact]
    public void CleanupPosters_LogsCompletionInformation()
    {
        using var context = new MaintenancePosterRepairContext();
        var logger = new ListLogger<MaintenanceController>();
        var controller = context.CreateController(logger);

        var result = controller.CleanupPosters();

        Assert.IsType<OkObjectResult>(result);
        Assert.True(logger.Contains(LogLevel.Information, "CleanupPosters completed"));
    }

    [Fact]
    public void PurgeLogs_LogsCompletionInformation()
    {
        using var context = new MaintenancePosterRepairContext();
        var logger = new ListLogger<MaintenanceController>();
        var controller = context.CreateController(logger);

        var result = controller.PurgeLogs(new MaintenanceController.PurgeLogsDto { Scope = "logs" });

        Assert.IsType<OkObjectResult>(result);
        Assert.True(logger.Contains(LogLevel.Information, "PurgeLogs completed"));
    }

    [Fact]
    public void DetectDuplicates_PurgeFalse_LogsCompletionInformation()
    {
        using var context = new MaintenancePosterRepairContext();
        context.CreateRelease("one.jpg");
        context.CreateRelease("two.jpg");
        var logger = new ListLogger<MaintenanceController>();
        var controller = context.CreateController(logger);

        var result = controller.DetectDuplicates(purge: false);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(logger.Contains(LogLevel.Information, "DetectDuplicates completed"));
    }

    [Fact]
    public void DetectDuplicates_PurgeTrue_LogsCompletionInformation()
    {
        using var context = new MaintenancePosterRepairContext();
        context.CreateRelease("one.jpg");
        context.CreateRelease("two.jpg");
        var logger = new ListLogger<MaintenanceController>();
        var controller = context.CreateController(logger);

        var result = controller.DetectDuplicates(purge: true);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(logger.Contains(LogLevel.Information, "DetectDuplicates completed"));
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

        public MaintenanceController CreateController(ILogger<MaintenanceController>? logger = null)
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
                logger ?? NullLogger<MaintenanceController>.Instance);
        }

        public long CreateRelease(string posterFile)
        {
            using var conn = Db.Open();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return conn.ExecuteScalar<long>(
                """
                INSERT INTO releases(
                  source_id, guid, title, title_clean, created_at_ts, published_at_ts, unified_category, media_type, poster_file, poster_updated_at_ts
                )
                VALUES(
                  @sourceId, @guid, 'The Matrix', 'the matrix', @ts, @ts, 'Film', 'movie', @posterFile, @ts
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

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }

        public bool Contains(LogLevel level, string messageFragment)
            => Entries.Any(e => e.Level == level && e.Message.Contains(messageFragment, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
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
