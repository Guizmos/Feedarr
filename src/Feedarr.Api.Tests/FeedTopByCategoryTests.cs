using System.Text.Json;
using Dapper;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Categories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class FeedTopByCategoryTests
{
    [Fact]
    public void Top_ReturnsOnlyCategoriesPresentInWindow_RespectsTake_AndUsesStableOrdering()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = InsertSource(db, "source-dynamic-top");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        InsertSourceCategory(db, sourceId, 2000, "Movies", unifiedKey: "films", unifiedLabel: "Films");
        InsertSourceCategory(db, sourceId, 5000, "TV", unifiedKey: "series", unifiedLabel: "Série TV");
        InsertSourceCategory(db, sourceId, 5070, "Anime", unifiedKey: "anime", unifiedLabel: "Anime");

        InsertRelease(
            db,
            sourceId,
            guid: "guid-film-1",
            categoryId: 2000,
            unifiedCategory: "Film",
            seeders: 90,
            categoryIds: "2000",
            title: "Film A",
            publishedAt: now - 900,
            createdAt: now - 30);
        InsertRelease(
            db,
            sourceId,
            guid: "guid-film-2",
            categoryId: 2000,
            unifiedCategory: "Film",
            seeders: 80,
            categoryIds: "2000",
            title: "Film B",
            publishedAt: now - 300,
            createdAt: now - 20);
        InsertRelease(
            db,
            sourceId,
            guid: "guid-film-3",
            categoryId: 2000,
            unifiedCategory: "Film",
            seeders: 70,
            categoryIds: "2000",
            title: "Film C",
            publishedAt: now - 300,
            createdAt: now - 10);
        InsertRelease(
            db,
            sourceId,
            guid: "guid-series-1",
            categoryId: 5000,
            unifiedCategory: "Serie",
            seeders: 60,
            categoryIds: "5000",
            title: "Serie A",
            publishedAt: now - 120,
            createdAt: now - 9);
        InsertRelease(
            db,
            sourceId,
            guid: "guid-series-2",
            categoryId: 5000,
            unifiedCategory: "Serie",
            seeders: 50,
            categoryIds: "5000",
            title: "Serie B",
            publishedAt: now - 60,
            createdAt: now - 8);
        InsertRelease(
            db,
            sourceId,
            guid: "guid-anime-old",
            categoryId: 5070,
            unifiedCategory: "Anime",
            seeders: 40,
            categoryIds: "5070",
            title: "Anime Old",
            publishedAt: now - (26 * 60 * 60),
            createdAt: now - 5);

        var controller = CreateController(db);
        var action = controller.Top(hours: 24, take: 2, limit: null, sourceId: null, indexerId: null);
        var ok = Assert.IsType<OkObjectResult>(action);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        Assert.Equal("published_at_ts", doc.RootElement.GetProperty("window").GetProperty("field").GetString());
        Assert.Equal(24, doc.RootElement.GetProperty("window").GetProperty("hours").GetInt32());

        var globalTop = doc.RootElement.GetProperty("globalTop").EnumerateArray().ToList();
        Assert.Equal(2, globalTop.Count);
        Assert.Equal("Serie B", globalTop[0].GetProperty("Title").GetString());
        Assert.Equal("Serie A", globalTop[1].GetProperty("Title").GetString());

        var categories = doc.RootElement.GetProperty("categories").EnumerateArray().ToList();
        Assert.Equal(2, categories.Count);
        Assert.Equal("films", categories[0].GetProperty("key").GetString());
        Assert.Equal(3, categories[0].GetProperty("count").GetInt32());
        Assert.Equal("series", categories[1].GetProperty("key").GetString());
        Assert.Equal(2, categories[1].GetProperty("count").GetInt32());

        var filmTop = categories[0].GetProperty("top").EnumerateArray().ToList();
        Assert.Equal(2, filmTop.Count);
        Assert.Equal("Film C", filmTop[0].GetProperty("Title").GetString());
        Assert.Equal("Film B", filmTop[1].GetProperty("Title").GetString());
    }

    [Fact]
    public void Top_UsesPublishedAtWindowLikeLibrary_NotCreatedAt()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = InsertSource(db, "source-window-check");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        InsertSourceCategory(db, sourceId, 2000, "Movies", unifiedKey: "films", unifiedLabel: "Films");
        InsertRelease(
            db,
            sourceId,
            guid: "guid-old-published",
            categoryId: 2000,
            unifiedCategory: "Film",
            seeders: 80,
            categoryIds: "2000",
            title: "Old Published",
            publishedAt: now - (48 * 60 * 60),
            createdAt: now - 5);
        InsertRelease(
            db,
            sourceId,
            guid: "guid-recent",
            categoryId: 2000,
            unifiedCategory: "Film",
            seeders: 70,
            categoryIds: "2000",
            title: "Recent Published",
            publishedAt: now - 120,
            createdAt: now - 4);

        var controller = CreateController(db);
        var action = controller.Top(hours: 24, take: 5, limit: null, sourceId: null, indexerId: null);
        var ok = Assert.IsType<OkObjectResult>(action);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var globalTop = doc.RootElement.GetProperty("globalTop").EnumerateArray().ToList();
        Assert.Single(globalTop);
        Assert.Equal("Recent Published", globalTop[0].GetProperty("Title").GetString());

        var categories = doc.RootElement.GetProperty("categories").EnumerateArray().ToList();
        Assert.Single(categories);
        Assert.Equal(1, categories[0].GetProperty("count").GetInt32());
    }

    [Fact]
    public void Top_UsesLibraryResolverFallbackForDynamicCategoryBuckets()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = InsertSource(db, "source-fallback-shows");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        InsertSourceCategory(db, sourceId, 9000, "Unclassified", unifiedKey: null, unifiedLabel: null);
        InsertRelease(
            db,
            sourceId,
            guid: "guid-fallback-show",
            categoryId: 9000,
            unifiedCategory: "",
            seeders: 65,
            categoryIds: "9000",
            title: "Daily Show",
            publishedAt: now - 60,
            createdAt: now - 2);

        var controller = CreateController(db);
        var action = controller.Top(hours: 24, take: 5, limit: null, sourceId: null, indexerId: null);
        var ok = Assert.IsType<OkObjectResult>(action);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var categories = doc.RootElement.GetProperty("categories").EnumerateArray().ToList();
        Assert.Single(categories);
        Assert.Equal("emissions", categories[0].GetProperty("key").GetString());
        Assert.Equal(1, categories[0].GetProperty("count").GetInt32());

        var first = categories[0].GetProperty("top").EnumerateArray().First();
        Assert.Equal("emissions", first.GetProperty("UnifiedCategoryKey").GetString());
    }

    [Fact]
    public void Top_OrdersSectionsByVolumeDesc_ThenLabelAsc()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = InsertSource(db, "source-ordering");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        InsertSourceCategory(db, sourceId, 2000, "Movies", unifiedKey: "films", unifiedLabel: "Zulu");
        InsertSourceCategory(db, sourceId, 5000, "TV", unifiedKey: "series", unifiedLabel: "Alpha");
        InsertSourceCategory(db, sourceId, 5070, "Anime", unifiedKey: "anime", unifiedLabel: "Mike");

        InsertRelease(
            db,
            sourceId,
            guid: "guid-order-film-1",
            categoryId: 2000,
            unifiedCategory: "Film",
            seeders: 50,
            categoryIds: "2000",
            title: "Film One",
            publishedAt: now - 300,
            createdAt: now - 30);
        InsertRelease(
            db,
            sourceId,
            guid: "guid-order-film-2",
            categoryId: 2000,
            unifiedCategory: "Film",
            seeders: 40,
            categoryIds: "2000",
            title: "Film Two",
            publishedAt: now - 240,
            createdAt: now - 20);
        InsertRelease(
            db,
            sourceId,
            guid: "guid-order-series-1",
            categoryId: 5000,
            unifiedCategory: "Serie",
            seeders: 60,
            categoryIds: "5000",
            title: "Series One",
            publishedAt: now - 180,
            createdAt: now - 10);
        InsertRelease(
            db,
            sourceId,
            guid: "guid-order-anime-1",
            categoryId: 5070,
            unifiedCategory: "Anime",
            seeders: 70,
            categoryIds: "5070",
            title: "Anime One",
            publishedAt: now - 120,
            createdAt: now - 5);

        var controller = CreateController(db);
        var action = controller.Top(hours: 24, take: 2, limit: null, sourceId: null, indexerId: null);
        var ok = Assert.IsType<OkObjectResult>(action);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var categories = doc.RootElement.GetProperty("categories").EnumerateArray().ToList();
        Assert.Equal(3, categories.Count);
        Assert.Equal("films", categories[0].GetProperty("key").GetString());
        Assert.Equal(2, categories[0].GetProperty("count").GetInt32());
        Assert.Equal("Anime", categories[1].GetProperty("label").GetString());
        Assert.Equal("Série TV", categories[2].GetProperty("label").GetString());
    }

    [Fact]
    public void Latest_KeepsReleaseVisible_WhenSourceCategoryRowIsMissing()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = InsertSource(db, "source-missing-category-row");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        InsertRelease(
            db,
            sourceId,
            guid: "guid-no-category-row",
            categoryId: 2000,
            unifiedCategory: "Film",
            seeders: 50,
            categoryIds: "2000",
            title: "Visible Without Category Row",
            publishedAt: now - 60,
            createdAt: now - 60);

        var controller = CreateController(db);
        var action = controller.Latest(sourceId, limit: 10, q: null, categoryId: null, minSeeders: null, seen: null);
        var ok = Assert.IsType<OkObjectResult>(action);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var rows = doc.RootElement.EnumerateArray().ToList();
        Assert.Single(rows);
        Assert.Equal("Visible Without Category Row", rows[0].GetProperty("Title").GetString());
        Assert.Equal("films", rows[0].GetProperty("UnifiedCategoryKey").GetString());
        Assert.Equal("Films", rows[0].GetProperty("UnifiedCategoryLabel").GetString());
    }

    [Fact]
    public void Latest_PreservesSourceCategoryMetadata_WhenSourceCategoryRowExists()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = InsertSource(db, "source-category-row-present");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        InsertSourceCategory(db, sourceId, 5000, "TV", unifiedKey: "series", unifiedLabel: "Serie TV");
        InsertRelease(
            db,
            sourceId,
            guid: "guid-with-category-row",
            categoryId: 5000,
            unifiedCategory: "Serie",
            seeders: 70,
            categoryIds: "5000",
            title: "Visible With Category Row",
            publishedAt: now - 60,
            createdAt: now - 60);

        var controller = CreateController(db);
        var action = controller.Latest(sourceId, limit: 10, q: null, categoryId: null, minSeeders: null, seen: null);
        var ok = Assert.IsType<OkObjectResult>(action);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var row = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("Visible With Category Row", row.GetProperty("Title").GetString());
        Assert.Equal("TV", row.GetProperty("CategoryName").GetString());
        Assert.Equal("series", row.GetProperty("UnifiedCategoryKey").GetString());
        Assert.Equal("Série TV", row.GetProperty("UnifiedCategoryLabel").GetString());
    }

    private static FeedController CreateController(Db db)
    {
        return new FeedController(
            db,
            new UnifiedCategoryService(),
            new MemoryCache(new MemoryCacheOptions()));
    }

    private static Db CreateDb(TestWorkspace workspace)
    {
        var options = OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        });
        return new Db(options);
    }

    private static long InsertSource(Db db, string name)
    {
        using var conn = db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return conn.ExecuteScalar<long>(
            """
            INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
            VALUES (@name, 1, @url, 'key', 'query', @now, @now);
            SELECT last_insert_rowid();
            """,
            new { name, url = $"http://localhost/{name}", now });
    }

    private static void InsertSourceCategory(
        Db db,
        long sourceId,
        int categoryId,
        string categoryName,
        string? unifiedKey,
        string? unifiedLabel)
    {
        using var conn = db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        conn.Execute(
            """
            INSERT INTO source_categories(source_id, cat_id, name, parent_cat_id, is_sub, last_seen_at_ts, unified_key, unified_label)
            VALUES (@sid, @cid, @name, NULL, 0, @now, @key, @label);
            """,
            new
            {
                sid = sourceId,
                cid = categoryId,
                name = categoryName,
                now,
                key = unifiedKey,
                label = unifiedLabel
            });
    }

    private static void InsertRelease(
        Db db,
        long sourceId,
        string guid,
        int categoryId,
        string unifiedCategory,
        int seeders,
        string categoryIds,
        string? title = null,
        long? publishedAt = null,
        long? createdAt = null)
    {
        using var conn = db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        conn.Execute(
            """
            INSERT INTO releases(
                source_id, guid, title, published_at_ts, category_id, unified_category, category_ids, seeders, grabs, created_at_ts
            )
            VALUES (
                @sid, @guid, @title, @publishedAt, @catId, @unified, @catIds, @seeders, 10, @createdAt
            );
            """,
            new
            {
                sid = sourceId,
                guid,
                title = string.IsNullOrWhiteSpace(title) ? $"Release {guid}" : title,
                publishedAt = publishedAt ?? now,
                catId = categoryId,
                unified = unifiedCategory,
                catIds = categoryIds,
                seeders,
                createdAt = createdAt ?? now
            });
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            RootDir = Path.Combine(Path.GetTempPath(), "feedarr-tests", Guid.NewGuid().ToString("N"));
            DataDir = Path.Combine(RootDir, "data");
            Directory.CreateDirectory(DataDir);
        }

        public string RootDir { get; }
        public string DataDir { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootDir))
                    Directory.Delete(RootDir, true);
            }
            catch
            {
            }
        }
    }
}
