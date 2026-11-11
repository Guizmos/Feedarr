using Dapper;
using Feedarr.Api.Services.Posters;
using Microsoft.Data.Sqlite;

namespace Feedarr.Api.Data.Repositories;

public sealed partial class ReleaseRepository
{
    internal PosterLifecycleGate PosterLifecycle => PosterLifecycleGate.ForDatabase(_db.DbPath);
    internal string HistoryDatabasePath => _db.DbPath;
    internal sealed record HistoryEntity(long Id, string? PosterFile);

    internal void ProtectManualEntity(long releaseId)
    {
        using var conn = _db.Open();
        conn.Execute("UPDATE media_entities SET retention_origin='manual' WHERE id=(SELECT entity_id FROM releases WHERE id=@releaseId)", new { releaseId });
    }

    // No create mode, migrations, writes, or Db.Open() filesystem side effects in a dry-run.
    internal SqliteConnection OpenHistoryRead()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = _db.DbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try { conn.Open(); return conn; }
        catch { conn.Dispose(); throw; }
    }

    private static string HistoryQuote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    private static readonly HashSet<string> HistoryKnownEntityColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "unified_category", "title_clean", "year", "poster_file", "poster_updated_at_ts", "created_at_ts", "updated_at_ts",
        "tmdb_id", "tvdb_id", "imdb_id", "ext_provider", "ext_provider_id", "ext_title", "ext_overview", "ext_tagline", "ext_genres",
        "ext_release_date", "ext_runtime_minutes", "ext_rating", "ext_votes", "ext_updated_at_ts", "ext_directors", "ext_writers", "ext_cast",
        "request_tmdb_id", "request_tmdb_status", "request_tmdb_updated_at_ts", "poster_key", "poster_store_dir", "retention_origin", "orphaned_at_ts"
    };

    private static string HistoryClassification(SqliteConnection conn, SqliteTransaction tx)
    {
        var unknownData = conn.Query("PRAGMA table_info(media_entities)", transaction: tx)
            .Select(row => (string)row.name).Where(name => !HistoryKnownEntityColumns.Contains(name))
            .Select(name => $"(me.{HistoryQuote(name)} IS NOT NULL AND me.{HistoryQuote(name)} IS NOT '')").ToList();
        var extraReferences = new HashSet<string>();
        foreach (var table in conn.Query<string>("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'", transaction: tx))
        {
            if (table is "releases" or "media_entity_arr_status" or "media_entities") continue;
            var columns = conn.Query($"PRAGMA table_info({HistoryQuote(table)})", transaction: tx).Select(row => (string)row.name);
            foreach (var column in columns.Where(name => name is "entity_id" or "media_entity_id"))
                extraReferences.Add($"EXISTS(SELECT 1 FROM {HistoryQuote(table)} dep WHERE dep.{HistoryQuote(column)}=me.id)");
            foreach (var fk in conn.Query($"PRAGMA foreign_key_list({HistoryQuote(table)})", transaction: tx))
                if (string.Equals((string)fk.table, "media_entities", StringComparison.OrdinalIgnoreCase))
                    extraReferences.Add($"EXISTS(SELECT 1 FROM {HistoryQuote(table)} dep WHERE dep.{HistoryQuote((string)fk.from)}=me.id)");
        }
        var extra = extraReferences.Count > 0 ? string.Join(" OR ", extraReferences) : "0";
        var unknown = unknownData.Count > 0 ? string.Join(" OR ", unknownData) : "0";
        return $"""
            WITH classified AS (
              SELECT me.id AS Id, me.poster_file AS PosterFile,
                CASE
                  WHEN EXISTS(SELECT 1 FROM releases r WHERE r.entity_id=me.id) THEN 'release'
                  WHEN me.retention_origin='manual' THEN 'manual_correction'
                  WHEN me.retention_origin <> 'automatic' THEN 'unknown_provenance'
                  WHEN EXISTS(SELECT 1 FROM media_entity_arr_status a WHERE a.entity_id=me.id) THEN 'arr_association'
                  WHEN {extra} THEN 'other_dependency'
                  WHEN me.request_tmdb_id IS NOT NULL OR COALESCE(me.request_tmdb_status,'')<>''
                    OR me.request_tmdb_updated_at_ts IS NOT NULL THEN 'request_identity'
                  WHEN lower(COALESCE(me.poster_file,'')) LIKE '%-manual.%'
                    OR EXISTS(SELECT 1 FROM poster_matches pm WHERE lower(COALESCE(pm.match_source,'')) LIKE '%manual%'
                      AND ((pm.poster_file COLLATE NOCASE=me.poster_file)
                        OR (pm.normalized_title=me.title_clean AND pm.year IS me.year))) THEN 'manual_metadata'
                  WHEN {unknown} THEN 'unknown_data'
                  WHEN me.orphaned_at_ts IS NULL OR me.orphaned_at_ts<=0 OR me.created_at_ts<=0 OR me.updated_at_ts<=0
                    OR max(me.orphaned_at_ts,me.created_at_ts,me.updated_at_ts,
                      COALESCE(me.poster_updated_at_ts,0),COALESCE(me.ext_updated_at_ts,0))>@cutoff THEN 'grace_period'
                  ELSE 'eligible'
                END AS Reason
              FROM media_entities me
            )
            """;
    }

    internal Dictionary<string, long> HistoryEntityCounts(long cutoff)
    {
        using var conn = OpenHistoryRead();
        using var tx = conn.BeginTransaction(deferred: true);
        return conn.Query<(string Reason, long Count)>(HistoryClassification(conn, tx) +
            "SELECT Reason,COUNT(*) FROM classified GROUP BY Reason", new { cutoff }, tx).ToDictionary(row => row.Reason, row => row.Count);
    }

    internal List<HistoryEntity> HistoryEntityCandidates(long cutoff, int limit)
    {
        using var conn = OpenHistoryRead();
        using var tx = conn.BeginTransaction(deferred: true);
        return conn.Query<HistoryEntity>(HistoryClassification(conn, tx) +
            "SELECT Id,PosterFile FROM classified WHERE Reason='eligible' ORDER BY Id LIMIT @limit", new { cutoff, limit }, tx).AsList();
    }

    internal int DeleteHistoryEntities(long[] ids, long cutoff)
    {
        if (ids.Length == 0) return 0;
        using var conn = _db.Open();
        conn.Execute("PRAGMA busy_timeout=500;");
        using var tx = conn.BeginTransaction(deferred: false);
        var current = conn.Query<long>(HistoryClassification(conn, tx) +
            "SELECT Id FROM classified WHERE Reason='eligible' AND Id IN @ids", new { ids, cutoff }, tx).ToArray();
        var count = current.Length == 0 ? 0 : conn.Execute("DELETE FROM media_entities WHERE id IN @current", new { current }, tx);
        if (count > 0)
            HistoryActivity(conn, tx, "Unused entities reclaimed", count);
        tx.Commit();
        return count;
    }

    internal List<string> HistoryStoreReferences()
    {
        using var conn = OpenHistoryRead();
        return conn.Query<string>("""
            SELECT poster_store_dir FROM releases WHERE COALESCE(poster_store_dir,'')<>''
            UNION ALL SELECT poster_store_dir FROM media_entities WHERE COALESCE(poster_store_dir,'')<>''
            """).AsList();
    }

    internal HashSet<string> HistoryReferencedPosterFiles(long[] excluded)
    {
        using var conn = OpenHistoryRead();
        using var tx = conn.BeginTransaction(deferred: true);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in conn.Query<string>("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'", transaction: tx).ToArray())
        {
            if (!conn.Query($"PRAGMA table_info({HistoryQuote(table)})", transaction: tx).Any(row => (string)row.name == "poster_file")) continue;
            var exclusion = table == "media_entities" && excluded.Length > 0 ? " AND id NOT IN @excluded" : "";
            foreach (var file in conn.Query<string>($"SELECT poster_file FROM {HistoryQuote(table)} WHERE COALESCE(poster_file,'')<>''{exclusion}", new { excluded }, tx))
                files.Add(file);
        }
        return files;
    }

    internal bool HistoryPosterReferenced(string file, long[]? projectedDeletedEntities = null)
    {
        using var conn = OpenHistoryRead();
        return HistoryPosterReferenced(conn, null, file, projectedDeletedEntities ?? []);
    }

    private static bool HistoryPosterReferenced(SqliteConnection conn, SqliteTransaction? tx, string file, long[] excluded)
    {
        // Cache entries (including unknown/manual matches) are not discarded to reclaim space.
        foreach (var table in conn.Query<string>("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'", transaction: tx).ToArray())
        {
            if (!conn.Query($"PRAGMA table_info({HistoryQuote(table)})", transaction: tx).Any(row => (string)row.name == "poster_file")) continue;
            var exclusion = table == "media_entities" && excluded.Length > 0 ? " AND id NOT IN @excluded" : "";
            if (conn.ExecuteScalar<long>($"SELECT EXISTS(SELECT 1 FROM {HistoryQuote(table)} WHERE poster_file COLLATE NOCASE=@file{exclusion})",
                new { file, excluded }, tx) != 0) return true;
        }
        return false;
    }

    private static void HistoryActivity(SqliteConnection conn, SqliteTransaction tx, string message, int count)
        => conn.Execute("""
            INSERT INTO activity_log(source_id,level,event_type,message,data_json,created_at_ts)
            VALUES(NULL,'info','history_cleanup',@message,@data,unixepoch())
            """, new { message, data = System.Text.Json.JsonSerializer.Serialize(new { count }) }, tx);

    internal bool DeleteHistoryPosterIfUnreferenced(string file, Action delete, bool audit = false)
    {
        using var conn = _db.Open();
        conn.Execute("PRAGMA busy_timeout=500;");
        using var tx = conn.BeginTransaction(deferred: false);
        if (HistoryPosterReferenced(conn, tx, file, [])) return false;
        delete(); // existing IPosterFileStore; no writer can publish a new DB reference before commit
        if (audit) HistoryActivity(conn, tx, "Unreferenced legacy poster reclaimed", 1);
        tx.Commit();
        return true;
    }
}
