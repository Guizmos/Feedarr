using Dapper;

namespace Feedarr.Api.Data;

public sealed class MigrationsRunner
{
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

            using var tx = conn.BeginTransaction();
            try
            {
                conn.Execute(sql, transaction: tx);

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
    }
}
