using Dapper;
using Feedarr.Api.Data;

namespace Feedarr.Api.Data.Repositories;

public sealed class ActivityRepository
{
    private readonly Db _db;
    private readonly Feedarr.Api.Services.BadgeSignal _signal;

    public ActivityRepository(Db db, Feedarr.Api.Services.BadgeSignal signal)
    {
        _db = db;
        _signal = signal;
    }

    public void Add(long? sourceId, string level, string eventType, string message, string? dataJson = null)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        conn.Execute(
            """
            INSERT INTO activity_log(source_id, level, event_type, message, data_json, created_at_ts)
            VALUES (@sid, @level, @etype, @msg, @data, @ts);
            """,
            new { sid = sourceId, level, etype = eventType, msg = message, data = dataJson, ts = now }
        );

        _signal.Notify("activity");
    }

    public IEnumerable<dynamic> List(int limit = 100, long? sourceId = null, string? eventType = null, string? level = null)
    {
        using var conn = _db.Open();
        limit = Math.Clamp(limit, 1, 500);

        var where = new List<string>();
        var args = new DynamicParameters();
        args.Add("lim", limit);

        if (sourceId is not null)
        {
            where.Add("source_id = @sid");
            args.Add("sid", sourceId.Value);
        }

        if (!string.IsNullOrWhiteSpace(eventType))
        {
            where.Add("event_type = @etype");
            args.Add("etype", eventType.Trim());
        }

        if (!string.IsNullOrWhiteSpace(level))
        {
            where.Add("level = @lvl");
            args.Add("lvl", level.Trim());
        }

        var whereSql = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : "";

        var sql = $"""
            SELECT id,
                   source_id as sourceId,
                   level,
                   event_type as eventType,
                   message,
                   data_json as dataJson,
                   created_at_ts as createdAt
            FROM activity_log
            {whereSql}
            ORDER BY created_at_ts DESC
            LIMIT @lim;
            """;

        return conn.Query(sql, args);
    }

    public void PurgeAll()
    {
        using var conn = _db.Open();
        conn.Execute("DELETE FROM activity_log;");
        _signal.Notify("activity");
    }

    public void PurgeHistory()
    {
        using var conn = _db.Open();
        conn.Execute(
            "DELETE FROM activity_log WHERE lower(event_type) = lower(@etype);",
            new { etype = "sync" }
        );
        _signal.Notify("activity");
    }

    public void PurgeLogs()
    {
        using var conn = _db.Open();
        conn.Execute(
            "DELETE FROM activity_log WHERE event_type IS NULL OR lower(event_type) <> lower(@etype);",
            new { etype = "sync" }
        );
        _signal.Notify("activity");
    }

    public int PurgeOlderThan(int days, string? scope)
    {
        using var conn = _db.Open();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, days)).ToUnixTimeSeconds();
        var normalized = string.IsNullOrWhiteSpace(scope) ? "all" : scope.Trim().ToLowerInvariant();

        var where = "created_at_ts < @cutoff";
        switch (normalized)
        {
            case "history":
                where += " AND lower(event_type) = lower(@etype)";
                break;
            case "logs":
                where += " AND (event_type IS NULL OR lower(event_type) <> lower(@etype))";
                break;
        }

        var deleted = conn.Execute(
            $"DELETE FROM activity_log WHERE {where};",
            new { cutoff, etype = "sync" }
        );
        _signal.Notify("activity");
        return deleted;
    }

    public int GetCount()
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<int>("SELECT COUNT(1) FROM activity_log;");
    }

    public void Purge()
    {
        PurgeAll();
    }

    public long GetLatestTsByEventType(string eventType)
    {
        using var conn = _db.Open();
        if (string.IsNullOrWhiteSpace(eventType)) return 0;
        return conn.ExecuteScalar<long?>(
            """
            SELECT MAX(created_at_ts)
            FROM activity_log
            WHERE event_type = @etype;
            """,
            new { etype = eventType.Trim() }
        ) ?? 0;
    }
}
