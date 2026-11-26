using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Services.DirectRss;
using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Data.Repositories;

public sealed class DirectRssRepository(Db db, IApiKeyProtectionService protection)
{
    public List<DirectRssFeed> List(long sourceId)
    {
        using var conn = db.Open();
        var feeds = conn.Query<DirectRssFeed>("""
            SELECT id Id, source_id SourceId, url_encrypted Url, name Name, category_key CategoryKey,
            enabled Enabled, last_status LastStatus, last_error LastError, last_checked_at_ts LastCheckedAt,
            last_item_count LastItemCount, potentially_saturated PotentiallySaturated
            FROM direct_rss_feeds WHERE source_id=@sourceId ORDER BY id
            """, new { sourceId }).ToList();
        foreach (var feed in feeds) feed.Url = protection.Unprotect(feed.Url);
        return feeds;
    }

    public long Save(long? sourceId, string name, string? color, IReadOnlyList<DirectRssFeed> feeds)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long id;
        if (sourceId is null)
        {
            id = conn.ExecuteScalar<long>("""
                INSERT INTO sources(name,enabled,torznab_url,api_key,auth_mode,color,created_at_ts,updated_at_ts,source_kind)
                VALUES(@name,1,@url,'','query',@color,@now,@now,'rss'); SELECT last_insert_rowid();
                """, new { name, color, now, url = "rss:" + Guid.NewGuid().ToString("N") }, tx);
        }
        else
        {
            id = sourceId.Value;
            if (conn.Execute("UPDATE sources SET name=@name,color=@color,updated_at_ts=@now WHERE id=@id AND source_kind='rss'",
                    new { id, name, color, now }, tx) != 1) throw new ArgumentException("RSS source not found");
        }
        var keep = feeds.Where(f => f.Id > 0).Select(f => f.Id).ToArray();
        conn.Execute("DELETE FROM direct_rss_feeds WHERE source_id=@id AND id NOT IN @keep", new { id, keep }, tx);
        foreach (var feed in feeds)
        {
            var url = protection.Protect(feed.Url);
            if (feed.Id == 0)
                conn.Execute("""
                    INSERT INTO direct_rss_feeds(source_id,url_encrypted,name,category_key,enabled)
                    VALUES(@id,@url,@Name,@CategoryKey,@Enabled)
                    """, new { id, url, feed.Name, feed.CategoryKey, feed.Enabled }, tx);
            else if (conn.Execute("""
                    UPDATE direct_rss_feeds SET url_encrypted=@url,name=@Name,category_key=@CategoryKey,enabled=@Enabled
                    WHERE id=@feedId AND source_id=@id
                    """, new { id, feedId = feed.Id, url, feed.Name, feed.CategoryKey, feed.Enabled }, tx) != 1)
                throw new ArgumentException("RSS feed not found");
        }
        tx.Commit();
        return id;
    }

    public void RecordFetch(long feedId, int count, string? error, bool saturated)
    {
        using var conn = db.Open();
        conn.Execute("""
            UPDATE direct_rss_feeds SET last_status=@status,last_error=@error,last_checked_at_ts=@now,
            last_item_count=@count,potentially_saturated=@saturated WHERE id=@feedId
            """, new { feedId, count, error, saturated, status = error is null ? "ok" : "error", now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
    }

    public Dictionary<string, string> ExistingCategories(long sourceId)
    {
        using var conn = db.Open();
        return conn.Query<(string guid, string category)>(
            "SELECT guid,top_category_key FROM releases WHERE source_id=@sourceId", new { sourceId })
            .ToDictionary(r => r.guid, r => r.category, StringComparer.Ordinal);
    }

    public void SaveProvenance(long sourceId, IEnumerable<DirectRssEntry> entries)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var entry in entries)
            conn.Execute("INSERT OR IGNORE INTO direct_rss_provenance(source_id,guid,feed_id) VALUES(@sourceId,@guid,@feedId)",
                entry.FeedIds.Select(feedId => new { sourceId, guid = entry.Item.Guid, feedId }), tx);
        tx.Commit();
    }
}
