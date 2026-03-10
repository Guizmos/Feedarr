using System.Text.Json;
using Dapper;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Titles;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class BadgesControllerSummaryTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Db CreateDb(TestWorkspace workspace)
        => new(OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        }));

    private static BadgesController BuildController(
        Db db,
        BackupExecutionCoordinator? coordinator = null)
    {
        var signal = new BadgeSignal();
        var releases = new ReleaseRepository(db, new TitleParser(), new UnifiedCategoryResolver());
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

        var cache = new MemoryCache(new MemoryCacheOptions());
        return new BadgesController(
            signal,
            releases,
            activity,
            settings,
            coordinator ?? new BackupExecutionCoordinator(),
            cache,
            NullLogger<BadgesController>.Instance);
    }

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
            new { sid = sourceId, guid, title = guid, createdAtTs });
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    /// <summary>
    /// Existing baseline: shape + values when sinceTs > 0 (exact count available).
    /// </summary>
    [Fact]
    public void Summary_ReturnsExpectedShapeAndValues()
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

        var controller = BuildController(db);
        var action = controller.Summary(activitySinceTs: 1005, releasesSinceTs: 1_700_000_150, activityLimit: 200);
        var ok = Assert.IsType<OkObjectResult>(action);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value, TestJson.CamelCase));
        var root = doc.RootElement;

        // activity
        Assert.True(root.TryGetProperty("activity", out var activityJson));
        Assert.Equal(2, activityJson.GetProperty("unreadCount").GetInt32());
        Assert.Equal(1020, activityJson.GetProperty("lastActivityTs").GetInt64());
        Assert.Equal("error", activityJson.GetProperty("tone").GetString());

        // releases — sinceTs > 0, so newSinceTsCount is provided → tone "info"
        Assert.True(root.TryGetProperty("releases", out var releasesJson));
        Assert.Equal(2, releasesJson.GetProperty("totalCount").GetInt32());
        Assert.Equal(1_700_000_200, releasesJson.GetProperty("latestTs").GetInt64());
        Assert.Equal(1, releasesJson.GetProperty("newSinceTsCount").GetInt32());
        Assert.Equal("info", releasesJson.GetProperty("tone").GetString());

        // system — no sync blocked, tone is null (JSON null)
        Assert.True(root.TryGetProperty("system", out var systemJson));
        Assert.Equal(1, systemJson.GetProperty("sourcesCount").GetInt32());
        Assert.False(systemJson.GetProperty("isSyncRunning").GetBoolean());
        Assert.False(systemJson.GetProperty("schedulerBusy").GetBoolean());
        Assert.Equal(JsonValueKind.Null, systemJson.GetProperty("tone").ValueKind);

        // settings
        Assert.True(root.TryGetProperty("settings", out var settingsJson));
        Assert.Equal(2, settingsJson.GetProperty("missingExternalCount").GetInt32());
        Assert.True(settingsJson.GetProperty("hasAdvancedMaintenanceEnabled").GetBoolean());
    }

    /// <summary>
    /// When releasesSinceTs == 0, newSinceTsCount must be null and tone must be "warn"
    /// (because latestTs > 0 — there are releases but no exact count of unseen ones).
    /// </summary>
    [Fact]
    public void Summary_ReleasesSinceTs_Zero_Returns_NullCount_And_WarnTone()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        using (var conn = db.Open())
        {
            var sourceId = InsertSource(conn, 1_700_000_000);
            InsertRelease(conn, sourceId, "guid-1", 1_700_000_100);
        }

        var controller = BuildController(db);
        // releasesSinceTs intentionally not provided (defaults to null → 0)
        var action = controller.Summary(activitySinceTs: 0, releasesSinceTs: null, activityLimit: 200);
        var ok = Assert.IsType<OkObjectResult>(action);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value, TestJson.CamelCase));
        var releases = doc.RootElement.GetProperty("releases");

        Assert.Equal(JsonValueKind.Null, releases.GetProperty("newSinceTsCount").ValueKind);
        Assert.Equal("warn", releases.GetProperty("tone").GetString());
    }

    /// <summary>
    /// When releasesSinceTs == 0 but there are NO releases in DB,
    /// latestTs is 0 so tone should be "info" (nothing to warn about).
    /// </summary>
    [Fact]
    public void Summary_ReleasesSinceTs_Zero_EmptyDb_Returns_InfoTone()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var controller = BuildController(db);
        var action = controller.Summary(activitySinceTs: 0, releasesSinceTs: null, activityLimit: 200);
        var ok = Assert.IsType<OkObjectResult>(action);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value, TestJson.CamelCase));
        var releases = doc.RootElement.GetProperty("releases");

        Assert.Equal(JsonValueKind.Null, releases.GetProperty("newSinceTsCount").ValueKind);
        Assert.Equal("info", releases.GetProperty("tone").GetString());
    }

    /// <summary>
    /// When releasesSinceTs > latestTs (client has seen everything),
    /// newSinceTsCount == 0 and tone is "info".
    /// </summary>
    [Fact]
    public void Summary_ReleasesSinceTs_AfterLatest_Returns_ZeroCount_And_InfoTone()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        using (var conn = db.Open())
        {
            var sourceId = InsertSource(conn, 1_700_000_000);
            InsertRelease(conn, sourceId, "guid-1", 1_700_000_100);
        }

        var controller = BuildController(db);
        // sinceTs is after the latest release
        var action = controller.Summary(activitySinceTs: 0, releasesSinceTs: 1_700_000_999, activityLimit: 200);
        var ok = Assert.IsType<OkObjectResult>(action);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value, TestJson.CamelCase));
        var releases = doc.RootElement.GetProperty("releases");

        Assert.Equal(0, releases.GetProperty("newSinceTsCount").GetInt32());
        Assert.Equal("info", releases.GetProperty("tone").GetString());
    }

    /// <summary>
    /// system.tone must be "warn" while a blocking operation (backup/restore) is in progress.
    /// </summary>
    [Fact]
    public async Task Summary_SystemTone_Warn_When_SyncBlocked()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var coordinator = new BackupExecutionCoordinator();

        // Start an exclusive operation that blocks until we release it.
        var entered = new SemaphoreSlim(0, 1);
        var hold = new SemaphoreSlim(0, 1);
        var exclusiveTask = coordinator.RunExclusiveAsync("test-backup", "test", async _ =>
        {
            entered.Release();
            await hold.WaitAsync();
            return 0;
        });

        // Wait until the coordinator has set SyncBlocked = true.
        var didEnter = await entered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(didEnter, "exclusive operation did not enter within timeout");

        try
        {
            var controller = BuildController(db, coordinator);
            var action = controller.Summary(activitySinceTs: 0, releasesSinceTs: null, activityLimit: 200);
            var ok = Assert.IsType<OkObjectResult>(action);

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value, TestJson.CamelCase));
            var system = doc.RootElement.GetProperty("system");

            Assert.True(system.GetProperty("schedulerBusy").GetBoolean());
            Assert.Equal("warn", system.GetProperty("tone").GetString());
        }
        finally
        {
            hold.Release();
            await exclusiveTask;
        }
    }

    /// <summary>
    /// system.tone is null (JSON null) in the nominal case (no operation running).
    /// </summary>
    [Fact]
    public void Summary_SystemTone_Null_When_Idle()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var controller = BuildController(db);
        var action = controller.Summary(activitySinceTs: 0, releasesSinceTs: null, activityLimit: 200);
        var ok = Assert.IsType<OkObjectResult>(action);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value, TestJson.CamelCase));
        var system = doc.RootElement.GetProperty("system");

        Assert.Equal(JsonValueKind.Null, system.GetProperty("tone").ValueKind);
    }

    // -------------------------------------------------------------------------
    // Test workspace
    // -------------------------------------------------------------------------

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
            catch { }
        }
    }
}

// Matches ASP.NET Core's default JSON serialization (camelCase property names).
// Used to parse OkObjectResult.Value the same way the HTTP response would look.
file static class TestJson
{
    public static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
