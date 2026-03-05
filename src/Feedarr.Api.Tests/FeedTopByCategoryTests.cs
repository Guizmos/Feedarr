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
        var action = controller.Top(hours: 24, take: 2, perCategoryTake: null, sort: null, dedupe: null, limit: null, sourceId: null, indexerId: null);
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
        var action = controller.Top(hours: 24, take: 5, perCategoryTake: null, sort: null, dedupe: null, limit: null, sourceId: null, indexerId: null);
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
    public void Top_CanBeCalledMultipleTimes_WithDifferentSortModes()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = InsertSource(db, "source-repeat-calls");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        InsertSourceCategory(db, sourceId, 2000, "Movies", unifiedKey: "films", unifiedLabel: "Films");
        InsertRelease(
            db,
            sourceId,
            guid: "guid-repeat-1",
            categoryId: 2000,
            unifiedCategory: "Film",
            seeders: 60,
            categoryIds: "2000",
            title: "Repeat One",
            publishedAt: now - 120,
            createdAt: now - 20);
        InsertRelease(
            db,
            sourceId,
            guid: "guid-repeat-2",
            categoryId: 2000,
            unifiedCategory: "Film",
            seeders: 40,
            categoryIds: "2000",
            title: "Repeat Two",
            publishedAt: now - 60,
            createdAt: now - 10);

        var controller = CreateController(db);
        var first = controller.Top(hours: 24, take: 5, perCategoryTake: null, sort: "recent", dedupe: "title_year", limit: null, sourceId: null, indexerId: null);
        var second = controller.Top(hours: 24, take: 5, perCategoryTake: null, sort: "score", dedupe: "title_year", limit: null, sourceId: null, indexerId: null);

        Assert.IsType<OkObjectResult>(first);
        Assert.IsType<OkObjectResult>(second);
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
        var action = controller.Top(hours: 24, take: 5, perCategoryTake: null, sort: null, dedupe: null, limit: null, sourceId: null, indexerId: null);
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
        var action = controller.Top(hours: 24, take: 2, perCategoryTake: null, sort: null, dedupe: null, limit: null, sourceId: null, indexerId: null);
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
    public void Top_DefaultRequest_MatchesExplicitRecentWithoutDedupe()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = InsertSource(db, "source-compat");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        InsertSourceCategory(db, sourceId, 2000, "Movies", unifiedKey: "films", unifiedLabel: "Films");
        InsertRelease(db, sourceId, "compat-a", 2000, "Film", 10, "2000", title: "Compat A", publishedAt: now - 300);
        InsertRelease(db, sourceId, "compat-b", 2000, "Film", 20, "2000", title: "Compat B", publishedAt: now - 120);
        InsertRelease(db, sourceId, "compat-c", 2000, "Film", 30, "2000", title: "Compat C", publishedAt: now - 60);

        var controller = CreateController(db);
        var defaultAction = controller.Top(hours: 24, take: 3, perCategoryTake: null, sort: null, dedupe: null, limit: null, sourceId: null, indexerId: null);
        var explicitAction = controller.Top(hours: 24, take: 3, perCategoryTake: 3, sort: "recent", dedupe: "none", limit: null, sourceId: null, indexerId: null);

        var defaultOk = Assert.IsType<OkObjectResult>(defaultAction);
        var explicitOk = Assert.IsType<OkObjectResult>(explicitAction);

        using var defaultDoc = JsonDocument.Parse(JsonSerializer.Serialize(defaultOk.Value));
        using var explicitDoc = JsonDocument.Parse(JsonSerializer.Serialize(explicitOk.Value));

        var defaultGlobal = defaultDoc.RootElement.GetProperty("globalTop").EnumerateArray().Select(x => x.GetProperty("Title").GetString()).ToList();
        var explicitGlobal = explicitDoc.RootElement.GetProperty("globalTop").EnumerateArray().Select(x => x.GetProperty("Title").GetString()).ToList();
        Assert.Equal(defaultGlobal, explicitGlobal);

        var defaultTop = defaultDoc.RootElement.GetProperty("categories").EnumerateArray().First().GetProperty("top").EnumerateArray().Select(x => x.GetProperty("Title").GetString()).ToList();
        var explicitTop = explicitDoc.RootElement.GetProperty("categories").EnumerateArray().First().GetProperty("top").EnumerateArray().Select(x => x.GetProperty("Title").GetString()).ToList();
        Assert.Equal(defaultTop, explicitTop);
        Assert.Equal("recent", explicitDoc.RootElement.GetProperty("sortUsed").GetString());
        Assert.Equal("none", explicitDoc.RootElement.GetProperty("dedupeUsed").GetString());
    }

    [Fact]
    public void Top_SupportsBusinessSortModes()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = InsertSource(db, "source-top-sorts");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        InsertSourceCategory(db, sourceId, 2000, "Movies", unifiedKey: "films", unifiedLabel: "Films");
        InsertRelease(db, sourceId, "sort-a", 2000, "Film", 50, "2000", title: "Recent Seeder", titleClean: "Recent Seeder", grabs: 1, mediaType: "movie", year: 2024, publishedAt: now - 30);
        InsertRelease(db, sourceId, "sort-b", 2000, "Film", 1, "2000", title: "Grab Leader", titleClean: "Grab Leader", grabs: 15, mediaType: "movie", year: 2024, publishedAt: now - 200);
        InsertRelease(db, sourceId, "sort-c", 2000, "Film", 40, "2000", title: "Balanced Hit", titleClean: "Balanced Hit", grabs: 5, mediaType: "movie", year: 2024, publishedAt: now - 120);

        var controller = CreateController(db);

        static List<string?> GetGlobalTitles(IActionResult action)
        {
            var ok = Assert.IsType<OkObjectResult>(action);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            return doc.RootElement.GetProperty("globalTop").EnumerateArray().Select(x => x.GetProperty("Title").GetString()).ToList();
        }

        var recentTitles = GetGlobalTitles(controller.Top(hours: 24, take: 3, perCategoryTake: 3, sort: "recent", dedupe: "none", limit: null, sourceId: null, indexerId: null));
        var grabsTitles = GetGlobalTitles(controller.Top(hours: 24, take: 3, perCategoryTake: 3, sort: "grabs", dedupe: "none", limit: null, sourceId: null, indexerId: null));
        var seedersTitles = GetGlobalTitles(controller.Top(hours: 24, take: 3, perCategoryTake: 3, sort: "seeders", dedupe: "none", limit: null, sourceId: null, indexerId: null));
        var scoreTitles = GetGlobalTitles(controller.Top(hours: 24, take: 3, perCategoryTake: 3, sort: "score", dedupe: "none", limit: null, sourceId: null, indexerId: null));

        Assert.Equal(new[] { "Recent Seeder", "Balanced Hit", "Grab Leader" }, recentTitles);
        Assert.Equal(new[] { "Grab Leader", "Balanced Hit", "Recent Seeder" }, grabsTitles);
        Assert.Equal(new[] { "Recent Seeder", "Balanced Hit", "Grab Leader" }, seedersTitles);
        Assert.Equal(new[] { "Grab Leader", "Balanced Hit", "Recent Seeder" }, scoreTitles);
    }

    [Fact]
    public void Top_DedupesByEntityId_WhenRequested()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = InsertSource(db, "source-dedupe-entity");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        InsertSourceCategory(db, sourceId, 2000, "Movies", unifiedKey: "films", unifiedLabel: "Films");
        InsertRelease(db, sourceId, "entity-a", 2000, "Film", 15, "2000", title: "Entity Duplicate A", titleClean: "Entity Duplicate", grabs: 2, mediaType: "movie", year: 2024, entityId: 42, publishedAt: now - 180);
        InsertRelease(db, sourceId, "entity-b", 2000, "Film", 3, "2000", title: "Entity Duplicate B", titleClean: "Entity Duplicate", grabs: 20, mediaType: "movie", year: 2024, entityId: 42, publishedAt: now - 120);
        InsertRelease(db, sourceId, "entity-c", 2000, "Film", 9, "2000", title: "Standalone", titleClean: "Standalone", grabs: 4, mediaType: "movie", year: 2024, entityId: 84, publishedAt: now - 60);

        var controller = CreateController(db);
        var action = controller.Top(hours: 24, take: 10, perCategoryTake: 10, sort: "grabs", dedupe: "entity", limit: null, sourceId: null, indexerId: null);
        var ok = Assert.IsType<OkObjectResult>(action);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var globalTop = doc.RootElement.GetProperty("globalTop").EnumerateArray().ToList();
        Assert.Equal(2, globalTop.Count);
        Assert.Equal("Entity Duplicate B", globalTop[0].GetProperty("Title").GetString());
        Assert.Equal("Standalone", globalTop[1].GetProperty("Title").GetString());

        var category = Assert.Single(doc.RootElement.GetProperty("categories").EnumerateArray());
        Assert.Equal(2, category.GetProperty("count").GetInt32());
        Assert.Equal("entity", doc.RootElement.GetProperty("dedupeUsed").GetString());
        Assert.True(doc.RootElement.GetProperty("supportsEntityDedupe").GetBoolean());
    }

    [Fact]
    public void Top_DedupesByTitleYear_WhenEntityIsMissing()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = InsertSource(db, "source-dedupe-title-year");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        InsertSourceCategory(db, sourceId, 2000, "Movies", unifiedKey: "films", unifiedLabel: "Films");
        InsertRelease(db, sourceId, "ty-a", 2000, "Film", 12, "2000", title: "Movie Alpha 1080p", titleClean: "Movie Alpha", grabs: 2, mediaType: "movie", year: 2023, publishedAt: now - 180);
        InsertRelease(db, sourceId, "ty-b", 2000, "Film", 4, "2000", title: "Movie Alpha 2160p", titleClean: "Movie Alpha", grabs: 10, mediaType: "movie", year: 2023, publishedAt: now - 120);
        InsertRelease(db, sourceId, "ty-c", 2000, "Film", 20, "2000", title: "Movie Beta", titleClean: "Movie Beta", grabs: 1, mediaType: "movie", year: 2023, publishedAt: now - 60);

        var controller = CreateController(db);
        var action = controller.Top(hours: 24, take: 10, perCategoryTake: 10, sort: "grabs", dedupe: "title_year", limit: null, sourceId: null, indexerId: null);
        var ok = Assert.IsType<OkObjectResult>(action);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var globalTitles = doc.RootElement.GetProperty("globalTop").EnumerateArray().Select(x => x.GetProperty("Title").GetString()).ToList();
        Assert.Equal(new[] { "Movie Alpha 2160p", "Movie Beta" }, globalTitles);

        var category = Assert.Single(doc.RootElement.GetProperty("categories").EnumerateArray());
        Assert.Equal(2, category.GetProperty("count").GetInt32());
    }

    [Fact]
    public void Top_RespectsPerCategoryTake_AfterBusinessRanking()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = InsertSource(db, "source-per-category");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        InsertSourceCategory(db, sourceId, 2000, "Movies", unifiedKey: "films", unifiedLabel: "Films");
        InsertSourceCategory(db, sourceId, 5000, "TV", unifiedKey: "series", unifiedLabel: "Série TV");

        InsertRelease(db, sourceId, "pc-film-a", 2000, "Film", 5, "2000", title: "Film A", titleClean: "Film A", grabs: 1, mediaType: "movie", year: 2024, publishedAt: now - 200);
        InsertRelease(db, sourceId, "pc-film-b", 2000, "Film", 10, "2000", title: "Film B", titleClean: "Film B", grabs: 8, mediaType: "movie", year: 2024, publishedAt: now - 150);
        InsertRelease(db, sourceId, "pc-film-c", 2000, "Film", 15, "2000", title: "Film C", titleClean: "Film C", grabs: 3, mediaType: "movie", year: 2024, publishedAt: now - 100);
        InsertRelease(db, sourceId, "pc-series-a", 5000, "Serie", 20, "5000", title: "Series A", titleClean: "Series A", grabs: 4, mediaType: "series", year: 2024, publishedAt: now - 90);
        InsertRelease(db, sourceId, "pc-series-b", 5000, "Serie", 12, "5000", title: "Series B", titleClean: "Series B", grabs: 9, mediaType: "series", year: 2024, publishedAt: now - 70);

        var controller = CreateController(db);
        var action = controller.Top(hours: 24, take: 10, perCategoryTake: 1, sort: "grabs", dedupe: "none", limit: null, sourceId: null, indexerId: null);
        var ok = Assert.IsType<OkObjectResult>(action);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var categories = doc.RootElement.GetProperty("categories").EnumerateArray().ToList();
        Assert.Equal(2, categories.Count);
        Assert.Single(categories[0].GetProperty("top").EnumerateArray());
        Assert.Single(categories[1].GetProperty("top").EnumerateArray());
        Assert.Equal("Film B", categories[0].GetProperty("top").EnumerateArray().First().GetProperty("Title").GetString());
        Assert.Equal("Series B", categories[1].GetProperty("top").EnumerateArray().First().GetProperty("Title").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("perCategoryTakeUsed").GetInt32());
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
        string? titleClean = null,
        int? year = null,
        string? mediaType = null,
        int? grabs = null,
        long? entityId = null,
        long? publishedAt = null,
        long? createdAt = null)
    {
        using var conn = db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        conn.Execute(
            """
            INSERT INTO releases(
                source_id, guid, title, title_clean, year, media_type, published_at_ts, category_id, unified_category, category_ids, seeders, grabs, entity_id, created_at_ts
            )
            VALUES (
                @sid, @guid, @title, @titleClean, @year, @mediaType, @publishedAt, @catId, @unified, @catIds, @seeders, @grabs, @entityId, @createdAt
            );
            """,
            new
            {
                sid = sourceId,
                guid,
                title = string.IsNullOrWhiteSpace(title) ? $"Release {guid}" : title,
                titleClean = titleClean,
                year,
                mediaType,
                publishedAt = publishedAt ?? now,
                catId = categoryId,
                unified = unifiedCategory,
                catIds = categoryIds,
                seeders,
                grabs = grabs ?? 10,
                entityId,
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
