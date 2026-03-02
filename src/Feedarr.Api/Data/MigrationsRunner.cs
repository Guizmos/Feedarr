using Dapper;
using Microsoft.Data.Sqlite;

namespace Feedarr.Api.Data;

public sealed class MigrationsRunner
{
    private sealed class TableInfoRow
    {
        public string Name { get; init; } = string.Empty;
    }

    private readonly Db _db;
    private readonly ILogger<MigrationsRunner> _log;

    public MigrationsRunner(Db db, ILogger<MigrationsRunner> log)
    {
        _db = db;
        _log = log;
    }

    public void Run()
    {
        using var conn = _db.Open();

        conn.Execute("""
            CREATE TABLE IF NOT EXISTS schema_migrations (
              id TEXT PRIMARY KEY,
              applied_at_ts INTEGER NOT NULL
            );
        """);

        var folder = Path.Combine(AppContext.BaseDirectory, "Data", "Migrations");
        if (!Directory.Exists(folder))
        {
            _log.LogWarning("Migrations folder not found: {Folder}", folder);
            return;
        }

        var files = Directory.GetFiles(folder, "*.sql")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            var id = Path.GetFileName(file);

            var already = conn.ExecuteScalar<long>(
                "SELECT COUNT(1) FROM schema_migrations WHERE id = @id",
                new { id }
            );

            if (already > 0) continue;

            var sql = File.ReadAllText(file);

            _log.LogInformation("Applying migration {Id}", id);

            // Migrations marked -- FEEDARR:NO-FK need FK=OFF so that a sources table
            // rebuild does not trigger the ON DELETE CASCADE on releases.
            // PRAGMA foreign_keys cannot be changed inside a transaction, so we toggle
            // it outside the transaction on the main connection.
            var isNoFk = sql.TrimStart().StartsWith("-- FEEDARR:NO-FK", StringComparison.OrdinalIgnoreCase);

            if (isNoFk) conn.Execute("PRAGMA foreign_keys=OFF;");
            try
            {
                using var tx = conn.BeginTransaction();
                try
                {
                    if (string.Equals(id, "0047_migration_guard_0005_group.sql", StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyMigration0047(conn, tx);
                    }
                    else
                    {
                        conn.Execute(sql, transaction: tx);
                    }

                    conn.Execute(
                        "INSERT INTO schema_migrations(id, applied_at_ts) VALUES (@id, @ts)",
                        new { id, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
                        tx
                    );

                    tx.Commit();
                }
                catch (Exception ex)
                {
                    tx.Rollback();
                    _log.LogError(ex, "Migration failed: {Id} ({File})", id, file);
                    throw; // stop le boot, sinon DB potentiellement incohérente
                }
            }
            finally
            {
                if (isNoFk) conn.Execute("PRAGMA foreign_keys=ON;");
            }
        }
    }

    private static void ApplyMigration0047(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!ColumnExists(conn, tx, "releases", "tvdb_id"))
        {
            conn.Execute("ALTER TABLE releases ADD COLUMN tvdb_id INTEGER;", transaction: tx);
        }

        conn.Execute(
            "CREATE INDEX IF NOT EXISTS idx_releases_tvdb_id ON releases(tvdb_id) WHERE tvdb_id IS NOT NULL;",
            transaction: tx);

        if (!ColumnExists(conn, tx, "source_categories", "unified_key"))
        {
            conn.Execute("ALTER TABLE source_categories ADD COLUMN unified_key TEXT;", transaction: tx);
        }

        if (!ColumnExists(conn, tx, "source_categories", "unified_label"))
        {
            conn.Execute("ALTER TABLE source_categories ADD COLUMN unified_label TEXT;", transaction: tx);
        }
    }

    private static bool ColumnExists(SqliteConnection conn, SqliteTransaction tx, string table, string column)
    {
        var rows = conn.Query<TableInfoRow>(
            $"PRAGMA table_info({table});",
            transaction: tx);

        return rows.Any(r => string.Equals(r.Name, column, StringComparison.OrdinalIgnoreCase));
    }
}
