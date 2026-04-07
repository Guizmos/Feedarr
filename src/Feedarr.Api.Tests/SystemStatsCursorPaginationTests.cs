using System.Text;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Feedarr.Api.Tests;

/// <summary>
/// Verifies keyset (cursor) pagination for StatsIndexers and StatsProviders.
/// </summary>
public sealed class SystemStatsCursorPaginationTests
{
    // ─── Test 1 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Two consecutive pages with direction=next must cover all rows without duplicates
    /// or gaps, regardless of SQL row-ordering non-determinism within a page.
    /// </summary>
    [Fact]
    public void StatsIndexers_CursorNext_NoDuplicatesAcrossPages()
    {
        using var workspace = new StatsTestWorkspace();
        var db = SystemStatsTestFactory.CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        // 2 sources × 3 categories each = 6 (sourceName, unifiedCategory) pairs
        SeedIndexerReleases(db);

        var ctrl = SystemStatsTestFactory.CreateController(db, workspace);

        // ── Page 1: no cursor → offset fallback, but cursor mode kicks in on first page too
        // Actually: no cursor → offset mode first page. We use limit=3 to split 6 rows into 2 pages.
        var r1 = Assert.IsType<OkObjectResult>(ctrl.StatsIndexers(limit: 3));
        var d1 = JsonDocument.Parse(JsonSerializer.Serialize(r1.Value!));
        var p1 = d1.RootElement.GetProperty("pagination");

        // No cursor → offset mode
        Assert.Equal("offset", p1.GetProperty("mode").GetString());

        var page1Rows = d1.RootElement.GetProperty("releasesByCategoryByIndexer")
            .EnumerateArray().ToList();
        Assert.Equal(3, page1Rows.Count);

        // Build a cursor for the last item of page 1 to navigate to page 2
        var last = page1Rows[^1];
        var lastSourceName     = last.GetProperty("sourceName").GetString()!;
        var lastCount          = last.GetProperty("count").GetInt32();
        var lastSourceId       = last.GetProperty("sourceId").GetInt64();
        var lastUnifiedCat     = last.GetProperty("unifiedCategory").GetString()!;
        var cursorToken        = MakeCursor(lastCount, lastSourceId, lastSourceName, lastUnifiedCat);

        // ── Page 2: use cursor
        var r2 = Assert.IsType<OkObjectResult>(
            ctrl.StatsIndexers(limit: 3, cursor: cursorToken, direction: "next"));
        var d2 = JsonDocument.Parse(JsonSerializer.Serialize(r2.Value!));
        var p2 = d2.RootElement.GetProperty("pagination");

        Assert.Equal("cursor", p2.GetProperty("mode").GetString());
        Assert.False(p2.GetProperty("hasMore").GetBoolean(), "page 2 should be the last page");

        var page2Rows = d2.RootElement.GetProperty("releasesByCategoryByIndexer")
            .EnumerateArray().ToList();
        Assert.Equal(3, page2Rows.Count);

        // No duplicates: all 6 (sourceName, unifiedCategory) pairs must be unique
        var allPairs = page1Rows.Concat(page2Rows)
            .Select(r => (r.GetProperty("sourceName").GetString()!, r.GetProperty("unifiedCategory").GetString()!))
            .ToList();

        Assert.Equal(6, allPairs.Distinct().Count());
    }

    // ─── Test 2 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Navigate forward from page 1 to page 2 using nextCursor, then navigate back
    /// using prevCursor. The back-navigation must yield the same items as page 1.
    /// </summary>
    [Fact]
    public void StatsProviders_CursorPrev_RoundTrip()
    {
        using var workspace = new StatsTestWorkspace();
        var db = SystemStatsTestFactory.CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        // 4 providers: extra1(20) > extra2(15) > tmdb(10) > tvmaze(8)
        // (sorted by matchedCount DESC, providerKey ASC)
        SeedProviderReleases(db, new[]
        {
            ("extra1", 20),
            ("extra2", 15),
            ("tmdb",   10),
            ("tvmaze",  8),
        });

        var ctrl = SystemStatsTestFactory.CreateController(db, workspace);

        // ── Get all 4 providers in offset mode to know the stable sort order
        var rAll = Assert.IsType<OkObjectResult>(ctrl.StatsProviders(limit: 100));
        var dAll = JsonDocument.Parse(JsonSerializer.Serialize(rAll.Value!));
        // Offset mode: _matchingByProvider is a JSON object {providerKey: count}
        var sortedKeys = dAll.RootElement.GetProperty("_matchingByProvider")
            .EnumerateObject()
            .OrderByDescending(p => p.Value.GetInt64())
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => p.Name)
            .ToList();

        // Expected order: extra1, extra2, tmdb, tvmaze
        Assert.Equal(4, sortedKeys.Count);

        // Build cursor for the last item of page 1 (limit=2: items[0] and items[1])
        // Page 1 in sort order = extra1, extra2
        var page1LastKey   = sortedKeys[1];                       // "extra2"
        var page1LastCount = GetProviderCount(dAll, page1LastKey); // 15
        var cursorToken    = MakeProviderCursor(page1LastCount, page1LastKey);

        // ── Navigate to page 2 (items at index 2 and 3 → tmdb, tvmaze)
        var r2 = Assert.IsType<OkObjectResult>(
            ctrl.StatsProviders(limit: 2, cursor: cursorToken, direction: "next"));
        var d2 = JsonDocument.Parse(JsonSerializer.Serialize(r2.Value!));

        var p2 = d2.RootElement.GetProperty("_pagination");
        Assert.Equal("cursor", p2.GetProperty("mode").GetString());

        // cursor mode: _matchingByProvider is a list [{providerKey, matchedCount}]
        var page2Items = d2.RootElement.GetProperty("_matchingByProvider")
            .EnumerateArray().ToList();
        Assert.Equal(2, page2Items.Count);
        var page2Keys = page2Items.Select(x => x.GetProperty("providerKey").GetString()!).ToList();
        Assert.Equal("tmdb",   page2Keys[0]);
        Assert.Equal("tvmaze", page2Keys[1]);

        // prevCursor from page 2 should navigate back to page 1
        var prevCursorToken = p2.GetProperty("prevCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(prevCursorToken));

        // ── Navigate back to page 1 using prevCursor
        var rBack = Assert.IsType<OkObjectResult>(
            ctrl.StatsProviders(limit: 2, cursor: prevCursorToken, direction: "prev"));
        var dBack = JsonDocument.Parse(JsonSerializer.Serialize(rBack.Value!));

        var backItems = dBack.RootElement.GetProperty("_matchingByProvider")
            .EnumerateArray().ToList();
        Assert.Equal(2, backItems.Count);
        var backKeys = backItems.Select(x => x.GetProperty("providerKey").GetString()!).ToList();

        // Round-trip: back-navigated items must match original page 1 order
        Assert.Equal(sortedKeys[0], backKeys[0]); // extra1
        Assert.Equal(sortedKeys[1], backKeys[1]); // extra2
    }

    // ─── Test 3 ─────────────────────────────────────────────────────────────

    /// <summary>Invalid cursor tokens must return 400 (no caching, no crash).</summary>
    [Fact]
    public void CursorInvalid_Returns400()
    {
        using var workspace = new StatsTestWorkspace();
        var db = SystemStatsTestFactory.CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();
        var ctrl = SystemStatsTestFactory.CreateController(db, workspace);

        // Garbled non-base64 input
        Assert.IsType<BadRequestObjectResult>(ctrl.StatsProviders(cursor: "!!not-base64!!"));
        Assert.IsType<BadRequestObjectResult>(ctrl.StatsIndexers(cursor: "!!bad!!"));

        // Valid base64 but not valid ProviderCursor JSON
        var garbage = Convert.ToBase64String(Encoding.UTF8.GetBytes("this is not json"))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        Assert.IsType<BadRequestObjectResult>(ctrl.StatsProviders(cursor: garbage));
        Assert.IsType<BadRequestObjectResult>(ctrl.StatsIndexers(cursor: garbage));
    }

    // ─── Seed helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds 2 sources × 3 categories each = 6 (sourceName, unifiedCategory) pairs.
    /// Stable sort order: Alpha Source (Film, Serie, Anime) then Beta Source (Film, Serie, Musique).
    /// </summary>
    private static void SeedIndexerReleases(Db db)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var conn = db.Open();

        conn.Execute(
            """
            INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
            VALUES ('Alpha Source', 1, 'http://localhost:9117/1', 'k1', 'query', @now, @now),
                   ('Beta Source',  1, 'http://localhost:9117/2', 'k2', 'query', @now, @now);
            """, new { now });

        var sources = conn.Query<(long id, string name)>(
            "SELECT id, name FROM sources ORDER BY name ASC;").ToList();
        var s1 = sources.First(s => s.name == "Alpha Source").id;
        var s2 = sources.First(s => s.name == "Beta Source").id;

        // Counts designed so pages are deterministic: Alpha→Film(5),Serie(3),Anime(2); Beta→Film(8),Serie(6),Musique(1)
        var rows = new (long srcId, string category, int count)[]
        {
            (s1, "Film",    5),
            (s1, "Serie",   3),
            (s1, "Anime",   2),
            (s2, "Film",    8),
            (s2, "Serie",   6),
            (s2, "Musique", 1),
        };

        var guid = 100;
        foreach (var (srcId, cat, cnt) in rows)
        {
            for (var i = 0; i < cnt; i++)
            {
                conn.Execute(
                    """
                    INSERT INTO releases(source_id, guid, title, created_at_ts, unified_category, grabs, seeders, size_bytes)
                    VALUES (@srcId, @gid, @title, @now, @cat, 0, 0, 0);
                    """,
                    new { srcId, gid = $"g-{guid}", title = $"T{guid}", now, cat });
                guid++;
            }
        }
    }

    private static void SeedProviderReleases(Db db, (string key, int count)[] providers)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var conn = db.Open();

        // Create a source if none exists
        if (conn.ExecuteScalar<long>("SELECT COUNT(1) FROM sources;") == 0)
        {
            conn.Execute(
                """
                INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
                VALUES ('S', 1, 'http://x', 'k', 'query', @now, @now);
                """, new { now });
        }
        var srcId = conn.ExecuteScalar<long>("SELECT id FROM sources ORDER BY id LIMIT 1;");

        var guid = 500;
        foreach (var (key, cnt) in providers)
        {
            for (var i = 0; i < cnt; i++)
            {
                conn.Execute(
                    """
                    INSERT INTO releases(source_id, guid, title, created_at_ts, poster_provider, grabs, seeders, size_bytes)
                    VALUES (@srcId, @gid, @title, @now, @key, 0, 0, 0);
                    """,
                    new { srcId, gid = $"p-{guid}", title = $"P{guid}", now, key });
                guid++;
            }
        }
    }

    // ─── Cursor-building helpers (reproduce CursorHelper.Encode for test use) ──

    private static string MakeCursor(int count, long sourceId, string sourceName, string unifiedCategory)
    {
        // JSON keys must match the ProviderCursor / IndexerCategoryCursor record property names
        // CursorHelper uses JsonSerializerDefaults.Web → camelCase property naming
        var json = $"{{\"sourceName\":{JsonSerializer.Serialize(sourceName)},\"count\":{count},\"sourceId\":{sourceId},\"unifiedCategory\":{JsonSerializer.Serialize(unifiedCategory)}}}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string MakeProviderCursor(long matchedCount, string providerKey)
    {
        var json = $"{{\"matchedCount\":{matchedCount},\"providerKey\":{JsonSerializer.Serialize(providerKey)}}}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static long GetProviderCount(JsonDocument doc, string key) =>
        doc.RootElement.GetProperty("_matchingByProvider").GetProperty(key).GetInt64();

}
