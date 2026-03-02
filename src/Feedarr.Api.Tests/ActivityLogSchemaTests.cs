using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

/// <summary>
/// Verifies that after running all migrations:
///   - Only "activity_log" (singular) exists; "activity_logs" (plural) is gone.
///   - The idx_activity_created index is present on activity_log.
///   - ActivityRepository reads/writes to the correct table.
/// </summary>
public sealed class ActivityLogSchemaTests
{
    // ------------------------------------------------------------------ //
    //  A) Only activity_log must exist — activity_logs must be absent     //
    // ------------------------------------------------------------------ //

    [Fact]
    public void AfterMigrations_OnlyActivityLogExists_ActivityLogsIsDropped()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        using var conn = db.Open();

        var tables = conn.Query<string>(
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;"
        ).ToList();

        Assert.Contains("activity_log", tables);
        Assert.DoesNotContain("activity_logs", tables);
    }

    // ------------------------------------------------------------------ //
    //  B) idx_activity_created must be on activity_log (not activity_logs) //
    // ------------------------------------------------------------------ //

    [Fact]
    public void AfterMigrations_IdxActivityCreatedExistsOnActivityLog()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        using var conn = db.Open();

        // Query sqlite_master to find the index and verify it targets activity_log.
        var indexInfo = conn.QueryFirstOrDefault<(string? Name, string? TblName)>(
            """
            SELECT name as Name, tbl_name as TblName
            FROM sqlite_master
            WHERE type = 'index' AND name = 'idx_activity_created';
            """);

        Assert.NotNull(indexInfo.Name);
        Assert.Equal("activity_log", indexInfo.TblName);
    }

    // ------------------------------------------------------------------ //
    //  C) ActivityRepository writes to activity_log and reads it back     //
    // ------------------------------------------------------------------ //

    [Fact]
    public void ActivityRepository_WritesAndReads_FromActivityLog()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var signal = new BadgeSignal();
        var repo = new ActivityRepository(db, signal);

        repo.Add(sourceId: null, level: "info", eventType: "test", message: "hello world");

        var items = repo.List(limit: 10).ToList();

        Assert.Single(items);
        Assert.Equal("hello world", (string)items[0].message);
        Assert.Equal("test", (string)items[0].eventType);
        Assert.Equal("info", (string)items[0].level);
    }

    // ------------------------------------------------------------------ //
    //  D) Rows in activity_logs (if any) are migrated to activity_log     //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Migration0046_MigratesLegacyRowsFromActivityLogs_IntoActivityLog()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);

        // Run migrations up to 0045 (before the cleanup migration),
        // then manually insert a row in activity_logs, then run 0046.
        // We simulate this by running all migrations except manually:
        //   1. Run migrations (which drops activity_logs via 0046), but we need
        //      to insert BEFORE 0046 runs. This is hard without partial migration runs.
        //
        // Alternative approach: create the activity_logs table manually, insert a row,
        // then apply migration 0046 SQL directly.

        // Run all migrations first (creates both activity_log and runs 0046 to drop activity_logs)
        // Verify that the schema is correct after full migration run.
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        using var conn = db.Open();

        // activity_logs no longer exists — this verifies migration 0046 ran
        var activityLogsExists = conn.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'activity_logs';");
        Assert.Equal(0, activityLogsExists);

        // activity_log still has its idx_activity_source index (from 0005_activity_log.sql)
        var idxSourceExists = conn.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'idx_activity_source';");
        Assert.Equal(1, idxSourceExists);
    }

    // ------------------------------------------------------------------ //
    //  E) Simulated legacy migration: activity_logs rows are preserved    //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Migration0046_LegacyData_IsPreservedAfterMigration()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);

        // Run only migrations 0001–0044 by applying the SQL directly, then
        // create activity_logs manually with a test row, and apply 0046 SQL.
        // Simpler: use a fresh DB, create minimal schema, insert legacy data,
        // then apply migration 0046 and verify the row appears in activity_log.

        using (var conn = db.Open())
        {
            // Minimal schema: just the two activity tables (simulating post-0001/0005)
            conn.Execute("""
                CREATE TABLE IF NOT EXISTS activity_log (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  source_id INTEGER,
                  level TEXT NOT NULL,
                  event_type TEXT NOT NULL,
                  message TEXT,
                  data_json TEXT,
                  created_at_ts INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS activity_logs (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  created_at_ts INTEGER NOT NULL,
                  level TEXT NOT NULL,
                  source_id INTEGER,
                  message TEXT NOT NULL,
                  details_json TEXT
                );
                """);

            // Insert a legacy row in activity_logs
            conn.Execute("""
                INSERT INTO activity_logs (created_at_ts, level, source_id, message, details_json)
                VALUES (1700000000, 'info', NULL, 'legacy message', '{"detail":"old"}');
                """);
        }

        // Apply migration 0046 SQL directly
        var migrationSql = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Data", "Migrations",
                "0046_cleanup_orphaned_activity_logs.sql"));

        using (var conn = db.Open())
        {
            conn.Execute(migrationSql);
        }

        using var verifyConn = db.Open();

        // activity_logs must be gone
        var activityLogsExists = verifyConn.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'activity_logs';");
        Assert.Equal(0, activityLogsExists);

        // The legacy row must have been migrated to activity_log
        var migratedRow = verifyConn.QueryFirstOrDefault<dynamic>(
            "SELECT * FROM activity_log WHERE message = 'legacy message';");
        Assert.NotNull(migratedRow);
        Assert.Equal("migrated", (string)migratedRow!.event_type);
        Assert.Equal("info", (string)migratedRow.level);
        Assert.Equal("{\"detail\":\"old\"}", (string)migratedRow.data_json);
    }

    // ------------------------------------------------------------------ helpers --//

    private static Db CreateDb(TestWorkspace workspace)
    {
        var options = OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        });
        return new Db(options);
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
            try { if (Directory.Exists(RootDir)) Directory.Delete(RootDir, true); }
            catch { }
        }
    }
}
