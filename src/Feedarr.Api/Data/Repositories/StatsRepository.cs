using Dapper;

namespace Feedarr.Api.Data.Repositories;

public sealed class StatsRepository
{
    private readonly Db _db;

    public StatsRepository(Db db) => _db = db;

    public long Get(string key)
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<long>(
            "SELECT value FROM stats WHERE key = @key",
            new { key }
        );
    }

    public void Set(string key, long value)
    {
        using var conn = _db.Open();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        conn.Execute(
            @"INSERT INTO stats (key, value, updated_at_ts) VALUES (@key, @value, @ts)
              ON CONFLICT(key) DO UPDATE SET value = @value, updated_at_ts = @ts",
            new { key, value, ts }
        );
    }

    public void Increment(string key, long delta = 1)
    {
        using var conn = _db.Open();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        conn.Execute(
            @"INSERT INTO stats (key, value, updated_at_ts) VALUES (@key, @delta, @ts)
              ON CONFLICT(key) DO UPDATE SET value = value + @delta, updated_at_ts = @ts",
            new { key, delta, ts }
        );
    }

    public Dictionary<string, long> GetAll()
    {
        using var conn = _db.Open();
        var rows = conn.Query<(string key, long value)>(
            "SELECT key, value FROM stats"
        );
        return rows.ToDictionary(r => r.key, r => r.value);
    }

    public Dictionary<string, long> GetByPrefix(string prefix)
    {
        using var conn = _db.Open();
        var rows = conn.Query<(string key, long value)>(
            "SELECT key, value FROM stats WHERE key LIKE @pattern",
            new { pattern = prefix + "%" }
        );
        return rows.ToDictionary(r => r.key, r => r.value);
    }
}
