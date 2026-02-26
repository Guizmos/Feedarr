using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Data.Repositories;

public sealed class SourceRepository
{
    private readonly Db _db;
    private readonly IApiKeyProtectionService _keyProtection;

    public SourceRepository(Db db, IApiKeyProtectionService keyProtection)
    {
        _db = db;
        _keyProtection = keyProtection;
    }

    public static bool TryNormalizeGroupKey(string? value, out string normalizedKey)
        => CategoryGroupCatalog.TryNormalizeKey(value, out normalizedKey);

    public static bool IsAllowedGroupKey(string? value)
        => TryNormalizeGroupKey(value, out _);

    public static string GetGroupLabel(string key)
    {
        return TryNormalizeGroupKey(key, out var normalized)
            ? CategoryGroupCatalog.LabelForKey(normalized)
            : key?.Trim() ?? "";
    }

    public long Create(string name, string torznabUrl, string apiKey, string authMode, long? providerId = null, string? color = null)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var encryptedKey = _keyProtection.Protect(apiKey);

        return conn.ExecuteScalar<long>(
            """
            INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, color, created_at_ts, updated_at_ts, provider_id)
            VALUES (@name, 1, @url, @key, @mode, @color, @now, @now, @providerId);
            SELECT last_insert_rowid();
            """,
            new { name, url = torznabUrl, key = encryptedKey, mode = authMode, color, now, providerId }
        );
    }

    public long? GetIdByTorznabUrl(string torznabUrl)
    {
        using var conn = _db.Open();
        return conn.QueryFirstOrDefault<long?>(
            "SELECT id FROM sources WHERE torznab_url = @url LIMIT 1;",
            new { url = torznabUrl }
        );
    }

    public IEnumerable<SourceListItem> List()
    {
        using var conn = _db.Open();
        return conn.Query<SourceListItem>(
            """
            SELECT
                id as Id,
                name as Name,
                enabled as Enabled,
                torznab_url as TorznabUrl,
                auth_mode as AuthMode,
                last_sync_at_ts as LastSyncAt,
                last_status as LastStatus,
                last_error as LastError,
                rss_mode as RssMode,
                provider_id as ProviderId,
                color as Color
            FROM sources
            ORDER BY id DESC;
            """
        );
    }

    /// <summary>
    /// Returns all enabled sources with decrypted API keys in a single query (for sync operations).
    /// </summary>
    public IList<Source> ListEnabledForSync()
    {
        using var conn = _db.Open();
        var sources = conn.Query<Source>(
            """
            SELECT
                id as Id,
                name as Name,
                enabled as Enabled,
                torznab_url as TorznabUrl,
                api_key as ApiKey,
                auth_mode as AuthMode,
                rss_mode as RssMode,
                last_sync_at_ts as LastSyncAt,
                provider_id as ProviderId,
                color as Color
            FROM sources
            WHERE enabled = 1
            ORDER BY id DESC;
            """
        ).ToList();

        foreach (var s in sources)
        {
            if (!string.IsNullOrEmpty(s.ApiKey))
                s.ApiKey = _keyProtection.Unprotect(s.ApiKey);
        }

        return sources;
    }

    public Source? Get(long id)
    {
        using var conn = _db.Open();
        var source = conn.QueryFirstOrDefault<Source>(
            """
            SELECT
                id as Id,
                name as Name,
                enabled as Enabled,
                torznab_url as TorznabUrl,
                api_key as ApiKey,
                auth_mode as AuthMode,
                rss_mode as RssMode,
                last_sync_at_ts as LastSyncAt,
                provider_id as ProviderId,
                color as Color
            FROM sources
            WHERE id = @id;
            """,
            new { id }
        );

        // Déchiffrer la clé API si présente
        if (source is not null && !string.IsNullOrEmpty(source.ApiKey))
            source.ApiKey = _keyProtection.Unprotect(source.ApiKey);

        return source;
    }

    public bool SetEnabled(long id, bool enabled)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var rows = conn.Execute(
            """
            UPDATE sources
            SET enabled = @en,
                updated_at_ts = @now
            WHERE id = @id;
            """,
            new { id, en = enabled ? 1 : 0, now }
        );

        return rows > 0;
    }

    /// <summary>
    /// Update "safe": si apiKeyOrNullIfNoChange == null => on ne touche pas api_key.
    /// </summary>
    public bool Update(long id, string name, string torznabUrl, string authMode, string? apiKeyOrNullIfNoChange, string? colorOrNullIfNoChange)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        int rows;
        if (apiKeyOrNullIfNoChange is null && colorOrNullIfNoChange is null)
        {
            rows = conn.Execute(
                """
                UPDATE sources
                SET name = @name,
                    torznab_url = @url,
                    auth_mode = @mode,
                    updated_at_ts = @now
                WHERE id = @id;
                """,
                new { id, name, url = torznabUrl, mode = authMode, now }
            );
        }
        else if (apiKeyOrNullIfNoChange is null)
        {
            rows = conn.Execute(
                """
                UPDATE sources
                SET name = @name,
                    torznab_url = @url,
                    auth_mode = @mode,
                    color = @color,
                    updated_at_ts = @now
                WHERE id = @id;
                """,
                new { id, name, url = torznabUrl, mode = authMode, color = colorOrNullIfNoChange, now }
            );
        }
        else if (colorOrNullIfNoChange is null)
        {
            var encryptedKey = _keyProtection.Protect(apiKeyOrNullIfNoChange);
            rows = conn.Execute(
                """
                UPDATE sources
                SET name = @name,
                    torznab_url = @url,
                    auth_mode = @mode,
                    api_key = @key,
                    updated_at_ts = @now
                WHERE id = @id;
                """,
                new { id, name, url = torznabUrl, mode = authMode, key = encryptedKey, now }
            );
        }
        else
        {
            var encryptedKey = _keyProtection.Protect(apiKeyOrNullIfNoChange);
            rows = conn.Execute(
                """
                UPDATE sources
                SET name = @name,
                    torznab_url = @url,
                    auth_mode = @mode,
                    api_key = @key,
                    color = @color,
                    updated_at_ts = @now
                WHERE id = @id;
                """,
                new { id, name, url = torznabUrl, mode = authMode, key = encryptedKey, color = colorOrNullIfNoChange, now }
            );
        }

        return rows > 0;
    }

    public void UpdateLastSync(long id, string status, string? error)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        conn.Execute(
            """
            UPDATE sources
            SET last_sync_at_ts = @now,
                last_status = @status,
                last_error = @error,
                updated_at_ts = @now
            WHERE id = @id;
            """,
            new { id, now, status, error }
        );
    }

    public void SaveRssMode(long id, string? rssMode)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        conn.Execute(
            "UPDATE sources SET rss_mode=@m, updated_at_ts=@now WHERE id=@id",
            new { id, m = rssMode, now }
        );
    }

    public void ReplaceCategories(long sourceId, IEnumerable<(int id, string name, bool isSub, int? parentId)> cats)
    {
        ReplaceCategories(sourceId, cats.Select(c => new SourceCategoryInput
        {
            Id = c.id,
            Name = c.name,
            IsSub = c.isSub,
            ParentId = c.parentId,
            UnifiedKey = null,
            UnifiedLabel = null
        }));
    }

    public void ReplaceCategories(long sourceId, IEnumerable<SourceCategoryInput> cats)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        conn.Execute("DELETE FROM source_categories WHERE source_id = @sid", new { sid = sourceId }, tx);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var normalized = NormalizeCategories(cats);

        if (normalized.Count > 0)
        {
            conn.Execute(
                """
                INSERT INTO source_categories(source_id, cat_id, name, parent_cat_id, is_sub, last_seen_at_ts, unified_key, unified_label)
                VALUES (@sid, @cid, @name, @parent, @isSub, @seenAt, @uKey, @uLabel);
                """,
                normalized.Select(c => new
                {
                    sid = sourceId,
                    cid = c.Id,
                    name = c.Name,
                    parent = c.ParentId,
                    isSub = c.IsSub ? 1 : 0,
                    seenAt = (long?)now,
                    uKey = c.UnifiedKey,
                    uLabel = c.UnifiedLabel
                }),
                tx
            );
        }

        tx.Commit();
    }

    public Dictionary<int, (string key, string label)> GetUnifiedCategoryMap(long sourceId)
        => GetCategoryMappingMap(sourceId);

    public Dictionary<int, (string key, string label)> GetCategoryMappingMap(long sourceId)
    {
        using var conn = _db.Open();
        var rows = conn.Query(
            """
            SELECT cat_id as id, group_key as groupKey, group_label as groupLabel
            FROM source_category_mappings
            WHERE source_id = @sid
              AND group_key IS NOT NULL
              AND group_key <> '';
            """,
            new { sid = sourceId }
        );

        var map = new Dictionary<int, (string key, string label)>();
        foreach (var row in rows)
        {
            var id = Convert.ToInt32(row.id);
            string key = row.groupKey ?? "";
            if (id <= 0)
                continue;

            if (!TryNormalizeGroupKey(key, out var normalizedKey))
                continue;

            map[id] = (normalizedKey, CategoryGroupCatalog.LabelForKey(normalizedKey));
        }

        return map;
    }

    public List<int> GetSelectedCategoryIds(long sourceId)
        => GetActiveCategoryIds(sourceId);

    public List<int> GetActiveCategoryIds(long sourceId)
    {
        using var conn = _db.Open();
        var rows = conn.Query<int>(
            """
            SELECT cat_id
            FROM source_category_mappings
            WHERE source_id = @sid
              AND group_key IS NOT NULL
              AND group_key <> ''
              AND cat_id > 0;
            """,
            new { sid = sourceId }
        );

        return rows.Distinct().ToList();
    }

    public List<SourceCategoryMapping> GetCategoryMappings(long sourceId)
    {
        using var conn = _db.Open();
        var rows = conn.Query(
            """
            SELECT cat_id as catId, group_key as groupKey, group_label as groupLabel
            FROM source_category_mappings
            WHERE source_id = @sid
              AND group_key IS NOT NULL
              AND group_key <> ''
            ORDER BY cat_id ASC;
            """,
            new { sid = sourceId }
        );

        var result = new List<SourceCategoryMapping>();
        foreach (var row in rows)
        {
            var catId = Convert.ToInt32(row.catId);
            var key = Convert.ToString(row.groupKey) ?? "";
            if (catId <= 0 || string.IsNullOrWhiteSpace(key)) continue;
            if (!TryNormalizeGroupKey(key, out string normalized)) continue;

            var label = CategoryGroupCatalog.LabelForKey(normalized);

            result.Add(new SourceCategoryMapping
            {
                CatId = catId,
                GroupKey = normalized,
                GroupLabel = label
            });
        }

        return result;
    }

    public int PatchCategoryMappings(long sourceId, IEnumerable<SourceCategoryMappingPatch> mappings)
    {
        var normalizedPatches = (mappings ?? Enumerable.Empty<SourceCategoryMappingPatch>())
            .Where(m => m is not null && m.CatId > 0)
            .GroupBy(m => m.CatId)
            .Select(g => g.Last())
            .ToList();

        if (normalizedPatches.Count == 0) return 0;

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var changed = 0;
        foreach (var patch in normalizedPatches)
        {
            if (string.IsNullOrWhiteSpace(patch.GroupKey))
            {
                changed += conn.Execute(
                    """
                    DELETE FROM source_category_mappings
                    WHERE source_id = @sid
                      AND cat_id = @catId;
                    """,
                    new { sid = sourceId, catId = patch.CatId },
                    tx
                );
                continue;
            }

            if (!TryNormalizeGroupKey(patch.GroupKey, out var normalizedKey))
                throw new ArgumentException(
                    $"Invalid category group key '{patch.GroupKey}' for catId={patch.CatId}.",
                    nameof(mappings));

            CategoryGroupCatalog.AssertCanonicalKey(normalizedKey);
            var label = CategoryGroupCatalog.LabelForKey(normalizedKey);
            changed += conn.Execute(
                """
                INSERT INTO source_category_mappings(
                  source_id, cat_id, group_key, group_label, created_at_ts, updated_at_ts
                )
                VALUES (@sid, @catId, @key, @label, @now, @now)
                ON CONFLICT(source_id, cat_id) DO UPDATE SET
                  group_key = excluded.group_key,
                  group_label = excluded.group_label,
                  updated_at_ts = excluded.updated_at_ts;
                """,
                new
                {
                    sid = sourceId,
                    catId = patch.CatId,
                    key = normalizedKey,
                    label,
                    now
                },
                tx
            );
        }

        tx.Commit();
        return changed;
    }

    public int Delete(long id)
    {
        using var conn = _db.Open();
        return conn.Execute("DELETE FROM sources WHERE id = @id", new { id });
    }

    public int DisableByProviderId(long providerId)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return conn.Execute(
            """
            UPDATE sources
            SET enabled = 0,
                updated_at_ts = @now
            WHERE provider_id = @pid
              AND enabled = 1;
            """,
            new { pid = providerId, now }
        );
    }

    public int CountByProviderId(long providerId)
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM sources WHERE provider_id = @pid;",
            new { pid = providerId }
        );
    }

    public sealed class SourceCategoryInput
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public bool IsSub { get; set; }
        public int? ParentId { get; set; }
        public string? UnifiedKey { get; set; }
        public string? UnifiedLabel { get; set; }
    }

    public sealed class SourceCategoryMapping
    {
        public int CatId { get; set; }
        public string GroupKey { get; set; } = "";
        public string GroupLabel { get; set; } = "";
    }

    public sealed class SourceCategoryMappingPatch
    {
        public int CatId { get; set; }
        public string? GroupKey { get; set; }
    }

    private static List<SourceCategoryInput> NormalizeCategories(IEnumerable<SourceCategoryInput> cats)
    {
        if (cats is null) return new List<SourceCategoryInput>();

        var map = new Dictionary<int, SourceCategoryInput>();

        foreach (var cat in cats)
        {
            if (cat is null) continue;
            if (cat.Id <= 0) continue;
            var name = (cat.Name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var normalized = new SourceCategoryInput
            {
                Id = cat.Id,
                Name = name,
                IsSub = cat.IsSub,
                ParentId = cat.ParentId,
                UnifiedKey = NormalizeNullable(cat.UnifiedKey),
                UnifiedLabel = NormalizeNullable(cat.UnifiedLabel)
            };

            if (!map.TryGetValue(normalized.Id, out var existing))
            {
                map[normalized.Id] = normalized;
                continue;
            }

            map[normalized.Id] = PickBest(existing, normalized);
        }

        return map.Values.OrderBy(c => c.Id).ToList();
    }

    private static SourceCategoryInput PickBest(SourceCategoryInput a, SourceCategoryInput b)
        => Score(b) > Score(a) ? b : a;

    private static int Score(SourceCategoryInput cat)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(cat.UnifiedKey)) score += 4;
        if (!string.IsNullOrWhiteSpace(cat.UnifiedLabel)) score += 2;
        if (cat.ParentId.HasValue) score += 1;
        if (cat.IsSub) score += 1;
        return score;
    }

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
