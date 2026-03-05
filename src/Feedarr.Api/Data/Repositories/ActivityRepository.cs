using Dapper;
using Feedarr.Api.Data;
using System.Dynamic;
using System.Text.Json;

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

        var rows = conn.Query<ActivityLogRow>(sql, args).ToList();
        EnrichSyncCategories(conn, rows);
        return rows.Select(MapToDynamic).ToList();
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

    private static dynamic MapToDynamic(ActivityLogRow row)
    {
        IDictionary<string, object?> result = new ExpandoObject();
        result["id"] = row.Id;
        result["sourceId"] = row.SourceId;
        result["level"] = row.Level;
        result["eventType"] = row.EventType;
        result["message"] = row.Message;
        result["dataJson"] = row.DataJson;
        result["createdAt"] = row.CreatedAt;
        if (string.Equals(row.EventType, "sync", StringComparison.OrdinalIgnoreCase))
            result["categories"] = row.Categories;
        return result;
    }

    private static void EnrichSyncCategories(System.Data.IDbConnection conn, IReadOnlyList<ActivityLogRow> rows)
    {
        if (rows.Count == 0)
            return;

        var sourceIds = new HashSet<long>();
        var catIds = new HashSet<int>();
        var parsedByRowId = new Dictionary<long, ParsedSyncCategories>();

        foreach (var row in rows)
        {
            if (!string.Equals(row.EventType, "sync", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!row.SourceId.HasValue || row.SourceId.Value <= 0)
            {
                row.Categories = [];
                continue;
            }

            var parsed = ParseSyncCategories(row.DataJson, row.Message);
            row.Categories = [];
            parsedByRowId[row.Id] = parsed;

            if (parsed.CategoryIds.Count == 0)
                continue;

            sourceIds.Add(row.SourceId.Value);
            foreach (var catId in parsed.CategoryIds)
                catIds.Add(catId);
        }

        var lookup = new Dictionary<(long sourceId, int catId), CategoryLookupRow>();
        if (sourceIds.Count > 0 && catIds.Count > 0)
        {
            var lookupRows = conn.Query<CategoryLookupRow>(
                """
                SELECT source_id as SourceId,
                       cat_id as CatId,
                       name as Name,
                       unified_key as UnifiedKey,
                       unified_label as UnifiedLabel
                FROM source_categories
                WHERE source_id IN @sourceIds
                  AND cat_id IN @catIds;
                """,
                new
                {
                    sourceIds = sourceIds.ToArray(),
                    catIds = catIds.ToArray()
                });

            foreach (var row in lookupRows)
            {
                lookup[(row.SourceId, row.CatId)] = row;
            }
        }

        foreach (var row in rows)
        {
            if (!string.Equals(row.EventType, "sync", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!row.SourceId.HasValue || row.SourceId.Value <= 0)
                continue;
            if (!parsedByRowId.TryGetValue(row.Id, out var parsed))
                continue;

            var categories = new List<ActivityCategoryDto>();
            var seen = new HashSet<int>();
            foreach (var catId in parsed.CategoryIds)
            {
                if (!seen.Add(catId))
                    continue;

                var hint = parsed.Hints.TryGetValue(catId, out var resolvedHint) ? resolvedHint : null;
                if (!lookup.TryGetValue((row.SourceId.Value, catId), out var lookedUp))
                {
                    categories.Add(new ActivityCategoryDto
                    {
                        Id = catId,
                        Key = NormalizeCategoryKey(hint?.Key),
                        Label = !string.IsNullOrWhiteSpace(hint?.Label) ? hint!.Label! : $"Cat {catId}"
                    });
                    continue;
                }

                categories.Add(new ActivityCategoryDto
                {
                    Id = catId,
                    Key = NormalizeCategoryKey(!string.IsNullOrWhiteSpace(hint?.Key) ? hint!.Key : lookedUp.UnifiedKey),
                    Label = PickLabel(hint?.Label, lookedUp.UnifiedLabel, lookedUp.Name, catId)
                });
            }

            row.Categories = categories;
        }
    }

    private static ParsedSyncCategories ParseSyncCategories(string? dataJson, string? message)
    {
        var ids = new List<int>();
        var hints = new Dictionary<int, CategoryHint>();
        var seen = new HashSet<int>();

        if (!string.IsNullOrWhiteSpace(dataJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(dataJson);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("categoryIds", out var categoryIds) && categoryIds.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var raw in categoryIds.EnumerateArray())
                        {
                            var catId = TryReadCategoryId(raw);
                            if (!catId.HasValue || !seen.Add(catId.Value))
                                continue;
                            ids.Add(catId.Value);
                        }
                    }

                    if (root.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var raw in categories.EnumerateArray())
                        {
                            if (raw.ValueKind != JsonValueKind.Object)
                                continue;

                            if (!raw.TryGetProperty("id", out var idElement))
                                continue;
                            var catId = TryReadCategoryId(idElement);
                            if (!catId.HasValue)
                                continue;

                            if (seen.Add(catId.Value))
                                ids.Add(catId.Value);

                            var key = raw.TryGetProperty("key", out var keyElement) && keyElement.ValueKind == JsonValueKind.String
                                ? keyElement.GetString()
                                : null;
                            var label = raw.TryGetProperty("label", out var labelElement) && labelElement.ValueKind == JsonValueKind.String
                                ? labelElement.GetString()
                                : null;
                            hints[catId.Value] = new CategoryHint(key, label);
                        }
                    }
                }
            }
            catch
            {
                // Ignore malformed JSON, fall back to legacy message format.
            }
        }

        if (ids.Count == 0)
        {
            foreach (var catId in ParseCategoryIdsFromLegacyMessage(message))
            {
                if (seen.Add(catId))
                    ids.Add(catId);
            }
        }

        return new ParsedSyncCategories(ids, hints);
    }

    private static IEnumerable<int> ParseCategoryIdsFromLegacyMessage(string? message)
    {
        var rawMessage = (message ?? string.Empty).ToLowerInvariant();
        var parts = rawMessage.Split("cats=");
        if (parts.Length < 2)
            return [];

        var catsSection = parts[1].Split("missing=")[0];
        if (string.IsNullOrWhiteSpace(catsSection))
            return [];

        var ids = new List<int>();
        foreach (var token in catsSection.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idToken = token.Split('=', StringSplitOptions.TrimEntries)[0];
            if (int.TryParse(idToken, out var parsedId) && parsedId > 0)
                ids.Add(parsedId);
        }

        return ids;
    }

    private static int? TryReadCategoryId(JsonElement raw)
    {
        return raw.ValueKind switch
        {
            JsonValueKind.Number when raw.TryGetInt32(out var id) && id > 0 => id,
            JsonValueKind.String when int.TryParse(raw.GetString(), out var id) && id > 0 => id,
            _ => null
        };
    }

    private static string? NormalizeCategoryKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var normalized = raw.Trim().ToLowerInvariant();
        return normalized.Length == 0 ? null : normalized;
    }

    private static string PickLabel(string? first, string? second, string? third, int catId)
    {
        if (!string.IsNullOrWhiteSpace(first))
            return first!;
        if (!string.IsNullOrWhiteSpace(second))
            return second!;
        if (!string.IsNullOrWhiteSpace(third))
            return third!;
        return $"Cat {catId}";
    }

    private sealed class ActivityLogRow
    {
        public long Id { get; init; }
        public long? SourceId { get; init; }
        public string? Level { get; init; }
        public string? EventType { get; init; }
        public string? Message { get; init; }
        public string? DataJson { get; init; }
        public long CreatedAt { get; init; }
        public IReadOnlyList<ActivityCategoryDto> Categories { get; set; } = [];
    }

    private sealed class CategoryLookupRow
    {
        public long SourceId { get; init; }
        public int CatId { get; init; }
        public string? Name { get; init; }
        public string? UnifiedKey { get; init; }
        public string? UnifiedLabel { get; init; }
    }

    private sealed class ActivityCategoryDto
    {
        public int Id { get; init; }
        public string Label { get; init; } = "";
        public string? Key { get; init; }
    }

    private sealed record CategoryHint(string? Key, string? Label);
    private sealed record ParsedSyncCategories(IReadOnlyList<int> CategoryIds, IReadOnlyDictionary<int, CategoryHint> Hints);
}
