using Dapper;
using Microsoft.Extensions.Caching.Memory;
using System.Text;

namespace Feedarr.Api.Data.Repositories;

public sealed record ProviderStatDelta(string Key, long Delta, long TotalAfterIncrement);

public interface IProviderStatsStore
{
    Dictionary<string, long> GetAll();
    Task IncrementProviderStatsBatchAsync(IReadOnlyList<ProviderStatDelta> rows, CancellationToken ct);
}

public sealed class StatsRepository : IProviderStatsStore
{
    // GetAll() is called once at startup by ProviderStatsService.EnsureLoaded()
    // and never again (guarded by its own _loaded flag). The cache here is a
    // safety net for any future callers that might call it on a hot path.
    private const string GetAllCacheKey = "stats:all:v1";
    private static readonly TimeSpan GetAllCacheDuration = TimeSpan.FromSeconds(30);

    private readonly Db _db;
    private readonly IMemoryCache _cache;

    public StatsRepository(Db db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

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
        _cache.Remove(GetAllCacheKey);
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
        _cache.Remove(GetAllCacheKey);
    }

    public Task IncrementProviderStatsBatchAsync(IReadOnlyList<ProviderStatDelta> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
            return Task.CompletedTask;

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sql = new StringBuilder(
            "INSERT INTO stats (key, value, updated_at_ts) VALUES ");
        var args = new DynamicParameters();
        args.Add("ts", ts);

        for (var i = 0; i < rows.Count; i++)
        {
            if (i > 0)
                sql.Append(',');

            sql.Append($"(@key{i}, @delta{i}, @ts)");
            args.Add($"key{i}", rows[i].Key);
            args.Add($"delta{i}", rows[i].Delta);
        }

        sql.Append(
            " ON CONFLICT(key) DO UPDATE SET value = value + excluded.value, updated_at_ts = excluded.updated_at_ts;");

        var command = new CommandDefinition(sql.ToString(), args, tx, cancellationToken: ct);
        return ExecuteBatchAsync(conn, tx, command);
    }

    private async Task ExecuteBatchAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        CommandDefinition command)
    {
        await conn.ExecuteAsync(command);
        tx.Commit();
        _cache.Remove(GetAllCacheKey);
    }

    /// <summary>
    /// Returns all stats key-value pairs.
    /// Results are cached for <see cref="GetAllCacheDuration"/> and invalidated
    /// on every <see cref="Set"/> or <see cref="Increment"/> call.
    /// </summary>
    public Dictionary<string, long> GetAll()
    {
        if (_cache.TryGetValue<Dictionary<string, long>>(GetAllCacheKey, out var cached) && cached is not null)
            return cached;

        using var conn = _db.Open();
        var rows = conn.Query<(string key, long value)>("SELECT key, value FROM stats");
        var result = rows.ToDictionary(r => r.key, r => r.value);
        _cache.Set(GetAllCacheKey, result, GetAllCacheDuration);
        return result;
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
