using System.Text.Json;
using Dapper;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Backup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class BadgesControllerSummaryTests
{
    [Fact]
    public async Task Summary_ReturnsExpectedShapeAndValues()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        using (var conn = db.Open())
        {
            var sourceId = InsertSource(conn, 1_700_000_000);
            InsertRelease(conn, sourceId, "guid-1", 1_700_000_100);
            InsertRelease(conn, sourceId, "guid-2", 1_700_000_200);

            conn.Execute(
                """
                INSERT INTO activity_log(source_id, level, event_type, message, data_json, created_at_ts)
                VALUES
                  (NULL, 'info', 'sync', 'info', NULL, 1000),
                  (NULL, 'warn', 'sync', 'warn', NULL, 1010),
                  (NULL, 'error', 'sync', 'error', NULL, 1020);
                """);
        }

        var signal = new BadgeSignal();
        var activity = new ActivityRepository(db, signal);
        var settings = new SettingsRepository(db);
        settings.SaveMaintenance(new MaintenanceSettings
        {
            MaintenanceAdvancedOptionsEnabled = true
        });
        settings.SaveExternalPartial(new ExternalSettings
        {
            TmdbApiKey = "tmdb-key"
        });

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var summaryCache = new BadgesSummaryCacheService(
            cache,
            new BadgesBaseSummaryProvider(
                db,
                settings,
                new BackupExecutionCoordinator()),
            OptionsFactory.Create(new AppOptions { BadgesSummaryCacheSeconds = 3 }),
            NullLogger<BadgesSummaryCacheService>.Instance);
        var controller = new BadgesController(
            signal,
            db,
            activity,
            summaryCache,
            NullLogger<BadgesController>.Instance);

        var action = await controller.Summary(activitySinceTs: 1005, releasesSinceTs: 1_700_000_150, activityLimit: 200);
        var ok = Assert.IsType<OkObjectResult>(action);
        Assert.Equal(200, ok.StatusCode ?? 200);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("activity", out var activityJson));
        Assert.Equal(2, activityJson.GetProperty("unreadCount").GetInt32());
        Assert.Equal(1020, activityJson.GetProperty("lastActivityTs").GetInt64());
        Assert.Equal("error", activityJson.GetProperty("tone").GetString());

        Assert.True(root.TryGetProperty("releases", out var releasesJson));
        Assert.Equal(2, releasesJson.GetProperty("totalCount").GetInt32());
        Assert.Equal(1_700_000_200, releasesJson.GetProperty("latestTs").GetInt64());
        Assert.Equal(1, releasesJson.GetProperty("newSinceTsCount").GetInt32());

        Assert.True(root.TryGetProperty("system", out var systemJson));
        Assert.Equal(1, systemJson.GetProperty("sourcesCount").GetInt32());
        Assert.False(systemJson.GetProperty("isSyncRunning").GetBoolean());
        Assert.False(systemJson.GetProperty("schedulerBusy").GetBoolean());

        Assert.True(root.TryGetProperty("settings", out var settingsJson));
        Assert.Equal(2, settingsJson.GetProperty("missingExternalCount").GetInt32());
        Assert.True(settingsJson.GetProperty("hasAdvancedMaintenanceEnabled").GetBoolean());
    }

    [Fact]
    public async Task Summary_DifferentClientParams_ReusesSharedBaseSummary()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        using (var conn = db.Open())
        {
            var sourceId = InsertSource(conn, 1_700_000_000);
            InsertRelease(conn, sourceId, "guid-a", 1_700_000_100);
            InsertRelease(conn, sourceId, "guid-b", 1_700_000_200);
            conn.Execute(
                """
                INSERT INTO activity_log(source_id, level, event_type, message, data_json, created_at_ts)
                VALUES
                  (NULL, 'info', 'sync', 'info', NULL, 1000),
                  (NULL, 'warn', 'sync', 'warn', NULL, 1010),
                  (NULL, 'error', 'sync', 'error', NULL, 1020);
                """);
        }

        var signal = new BadgeSignal();
        var activity = new ActivityRepository(db, signal);
        var baseSummary = new BadgesBaseSummary(
            LastActivityTs: 1020,
            SourcesCount: 1,
            ReleasesCount: 2,
            ReleasesLatestTs: 1_700_000_200,
            IncludeInfo: true,
            IncludeWarn: true,
            IncludeError: true,
            MissingExternalCount: 1,
            HasAdvancedMaintenanceEnabled: false,
            IsSyncRunning: false,
            SchedulerBusy: false,
            UpdatesBadge: false);
        var provider = new FakeBadgesBaseSummaryProvider(async _ =>
        {
            await Task.Yield();
            return baseSummary;
        });

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var summaryCache = new BadgesSummaryCacheService(
            cache,
            provider,
            OptionsFactory.Create(new AppOptions { BadgesSummaryCacheSeconds = 3 }),
            NullLogger<BadgesSummaryCacheService>.Instance);
        var controller = new BadgesController(
            signal,
            db,
            activity,
            summaryCache,
            NullLogger<BadgesController>.Instance);

        var first = await controller.Summary(activitySinceTs: 1000, releasesSinceTs: 1_700_000_050, activityLimit: 200);
        var second = await controller.Summary(activitySinceTs: 1010, releasesSinceTs: 1_700_000_150, activityLimit: 50);

        Assert.IsType<OkObjectResult>(first);
        Assert.IsType<OkObjectResult>(second);
        Assert.Equal(1, provider.CallCount);
    }

    private static Db CreateDb(TestWorkspace workspace)
        => new(OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        }));

    private static long InsertSource(Microsoft.Data.Sqlite.SqliteConnection conn, long ts)
    {
        conn.Execute(
            """
            INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
            VALUES ('Source A', 1, 'https://example.test', 'k', 'query', @ts, @ts);
            """,
            new { ts });
        return conn.ExecuteScalar<long>("SELECT id FROM sources LIMIT 1;");
    }

    private static void InsertRelease(Microsoft.Data.Sqlite.SqliteConnection conn, long sourceId, string guid, long createdAtTs)
    {
        conn.Execute(
            """
            INSERT INTO releases(source_id, guid, title, published_at_ts, created_at_ts)
            VALUES (@sid, @guid, @title, @createdAtTs, @createdAtTs);
            """,
            new
            {
                sid = sourceId,
                guid,
                title = guid,
                createdAtTs
            });
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            RootDir = Path.Combine(Path.GetTempPath(), "feedarr-badges-summary-tests", Guid.NewGuid().ToString("N"));
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
}
