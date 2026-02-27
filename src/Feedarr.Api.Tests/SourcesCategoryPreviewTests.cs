using Dapper;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Dtos.Sources;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Titles;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class SourcesCategoryPreviewTests
{
    // -------------------------------------------------------------------------
    // 400 — catId manquant (null)
    // -------------------------------------------------------------------------
    [Fact]
    public async Task GetCategoryPreview_CatIdNull_Returns400()
    {
        using var workspace = new TestWorkspace();
        var (db, sources, releases, controller) = CreateStack(workspace);

        var result = await controller.GetCategoryPreview(99, catId: null, limit: null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // -------------------------------------------------------------------------
    // 400 — catId <= 0
    // -------------------------------------------------------------------------
    [Fact]
    public async Task GetCategoryPreview_CatIdZero_Returns400()
    {
        using var workspace = new TestWorkspace();
        var (db, sources, releases, controller) = CreateStack(workspace);

        var result = await controller.GetCategoryPreview(99, catId: 0, limit: null, CancellationToken.None);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var error = bad.Value?.GetType().GetProperty("error")?.GetValue(bad.Value)?.ToString();
        Assert.Equal("catId missing or invalid", error);
    }

    [Fact]
    public async Task GetCategoryPreview_CatIdNegative_Returns400()
    {
        using var workspace = new TestWorkspace();
        var (db, sources, releases, controller) = CreateStack(workspace);

        var result = await controller.GetCategoryPreview(99, catId: -5, limit: null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // -------------------------------------------------------------------------
    // 404 — source inconnue
    // -------------------------------------------------------------------------
    [Fact]
    public async Task GetCategoryPreview_UnknownSource_Returns404()
    {
        using var workspace = new TestWorkspace();
        var (db, sources, releases, controller) = CreateStack(workspace);

        var result = await controller.GetCategoryPreview(99999, catId: 2000, limit: null, CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // -------------------------------------------------------------------------
    // 200 — liste vide (catégorie sans releases)
    // -------------------------------------------------------------------------
    [Fact]
    public async Task GetCategoryPreview_NoMatchingReleases_Returns200EmptyList()
    {
        using var workspace = new TestWorkspace();
        var (db, sources, releases, controller) = CreateStack(workspace);

        var sourceId = sources.Create("TestSource", "http://localhost:9117/api", "key", "query");

        // Release dans une catégorie différente
        InsertRelease(db, sourceId, "guid-other", categoryId: 5000, publishedAtTs: 1000, title: "Other Cat Release");

        var result = await controller.GetCategoryPreview(sourceId, catId: 2000, limit: null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<CategoryPreviewItemDto>>(ok.Value);
        Assert.Empty(items);
    }

    // -------------------------------------------------------------------------
    // 200 — résultats triés DESC par published_at_ts
    // -------------------------------------------------------------------------
    [Fact]
    public async Task GetCategoryPreview_ReturnsResultsOrderedByPublishedDesc()
    {
        using var workspace = new TestWorkspace();
        var (db, sources, releases, controller) = CreateStack(workspace);

        var sourceId = sources.Create("MyIndexer", "http://localhost:9117/api", "key", "query");

        var ts1 = 1_000_000L;
        var ts2 = 2_000_000L;
        var ts3 = 3_000_000L;

        // Insérer dans un ordre quelconque
        InsertRelease(db, sourceId, "guid-b", categoryId: 2040, publishedAtTs: ts2, title: "Release B");
        InsertRelease(db, sourceId, "guid-c", categoryId: 2040, publishedAtTs: ts3, title: "Release C");
        InsertRelease(db, sourceId, "guid-a", categoryId: 2040, publishedAtTs: ts1, title: "Release A");

        var result = await controller.GetCategoryPreview(sourceId, catId: 2040, limit: 20, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<CategoryPreviewItemDto>>(ok.Value);

        Assert.Equal(3, items.Count);
        // DESC: C (ts3) > B (ts2) > A (ts1)
        Assert.Equal(ts3, items[0].PublishedAtTs);
        Assert.Equal("Release C", items[0].Title);
        Assert.Equal(ts2, items[1].PublishedAtTs);
        Assert.Equal(ts1, items[2].PublishedAtTs);
    }

    // -------------------------------------------------------------------------
    // 200 — champs mappés correctement (sourceName, title, sizeBytes, categoryId)
    // -------------------------------------------------------------------------
    [Fact]
    public async Task GetCategoryPreview_FieldsMappedCorrectly()
    {
        using var workspace = new TestWorkspace();
        var (db, sources, releases, controller) = CreateStack(workspace);

        var sourceId = sources.Create("Indexer YGEGE", "http://localhost:9117/api", "key", "query");

        InsertRelease(db, sourceId, "guid-1", categoryId: 2000,
            publishedAtTs: 9_000_000L, title: "Big Movie 4K", sizeBytes: 8_589_934_592L, seeders: 42);

        var result = await controller.GetCategoryPreview(sourceId, catId: 2000, limit: null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<CategoryPreviewItemDto>>(ok.Value);

        Assert.Single(items);
        var item = items[0];
        Assert.Equal("Indexer YGEGE", item.SourceName);
        Assert.Equal("Big Movie 4K", item.Title);
        Assert.Equal(8_589_934_592L, item.SizeBytes);
        Assert.Equal(2000, item.CategoryId);
        Assert.Equal(42, item.Seeders);
    }

    // -------------------------------------------------------------------------
    // Strict filter — std_category_id/spec_category_id ne doivent PAS être utilisés
    // -------------------------------------------------------------------------
    [Fact]
    public async Task GetCategoryPreview_StrictFilter_IgnoresStdCategoryId()
    {
        using var workspace = new TestWorkspace();
        var (db, sources, releases, controller) = CreateStack(workspace);

        var sourceId = sources.Create("Source", "http://localhost:9117/api", "key", "query");

        // category_id = 100045, std_category_id = 2000 : ne doit PAS remonter pour catId=2000
        InsertRelease(db, sourceId, "guid-std", categoryId: 100045, publishedAtTs: 500L,
            title: "Should Not Match", stdCategoryId: 2000);

        var result = await controller.GetCategoryPreview(sourceId, catId: 2000, limit: null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<CategoryPreviewItemDto>>(ok.Value);
        Assert.Empty(items);
    }

    [Fact]
    public async Task GetCategoryPreview_StrictFilter_OnlyPrimaryCategoryId()
    {
        using var workspace = new TestWorkspace();
        var (db, sources, releases, controller) = CreateStack(workspace);

        var sourceId = sources.Create("Source", "http://localhost:9117/api", "key", "query");

        // catId=2000 doit ramener UNIQUEMENT la release dont category_id=2000
        InsertRelease(db, sourceId, "guid-target", categoryId: 2000, publishedAtTs: 1000L, title: "Target");
        InsertRelease(db, sourceId, "guid-bleed",  categoryId: 131681, publishedAtTs: 900L,
            title: "Should Not Appear", stdCategoryId: 2000);

        var result = await controller.GetCategoryPreview(sourceId, catId: 2000, limit: null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<CategoryPreviewItemDto>>(ok.Value);

        Assert.Single(items);
        Assert.Equal("Target", items[0].Title);
        Assert.Equal(2000, items[0].CategoryId);
    }

    [Fact]
    public async Task GetCategoryPreview_StrictFilter_MatchesCategoryIdsCsv()
    {
        using var workspace = new TestWorkspace();
        var (db, sources, releases, controller) = CreateStack(workspace);

        var sourceId = sources.Create("Source", "http://localhost:9117/api", "key", "query");

        // Release avec category_ids CSV contenant 2000 → doit remonter pour catId=2000
        InsertRelease(db, sourceId, "guid-csv", categoryId: 5040, publishedAtTs: 700L,
            title: "CSV Match", categoryIds: "5040,2000,2040");

        var result = await controller.GetCategoryPreview(sourceId, catId: 2000, limit: null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<CategoryPreviewItemDto>>(ok.Value);
        Assert.Single(items);
        Assert.Equal("CSV Match", items[0].Title);
    }

    // -------------------------------------------------------------------------
    // 200 — limit respecté (cap 50)
    // -------------------------------------------------------------------------
    [Fact]
    public async Task GetCategoryPreview_LimitCappedAt50()
    {
        using var workspace = new TestWorkspace();
        var (db, sources, releases, controller) = CreateStack(workspace);

        var sourceId = sources.Create("Source", "http://localhost:9117/api", "key", "query");

        for (var i = 0; i < 60; i++)
            InsertRelease(db, sourceId, $"guid-{i}", categoryId: 3000, publishedAtTs: i, title: $"Release {i}");

        var result = await controller.GetCategoryPreview(sourceId, catId: 3000, limit: 999, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<CategoryPreviewItemDto>>(ok.Value);
        Assert.Equal(50, items.Count);
    }

    // =========================================================================
    // Live endpoint — 400 / 404 (paths that never reach TorznabClient)
    // =========================================================================

    [Fact]
    public async Task GetCategoryPreviewLive_CatIdNull_Returns400()
    {
        using var workspace = new TestWorkspace();
        var (_, _, _, controller) = CreateStack(workspace);

        var result = await controller.GetCategoryPreviewLive(99, catId: null, limit: null, CancellationToken.None);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var error = bad.Value?.GetType().GetProperty("error")?.GetValue(bad.Value)?.ToString();
        Assert.Equal("catId missing or invalid", error);
    }

    [Fact]
    public async Task GetCategoryPreviewLive_CatIdZero_Returns400()
    {
        using var workspace = new TestWorkspace();
        var (_, _, _, controller) = CreateStack(workspace);

        var result = await controller.GetCategoryPreviewLive(99, catId: 0, limit: null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetCategoryPreviewLive_CatIdNegative_Returns400()
    {
        using var workspace = new TestWorkspace();
        var (_, _, _, controller) = CreateStack(workspace);

        var result = await controller.GetCategoryPreviewLive(99, catId: -1, limit: null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetCategoryPreviewLive_UnknownSource_Returns404()
    {
        using var workspace = new TestWorkspace();
        var (_, _, _, controller) = CreateStack(workspace);

        var result = await controller.GetCategoryPreviewLive(99999, catId: 2000, limit: null, CancellationToken.None);
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var error = notFound.Value?.GetType().GetProperty("error")?.GetValue(notFound.Value)?.ToString();
        Assert.Equal("source not found", error);
    }

    // =========================================================================
    // GetCategoryNameMap — DB lookup catId → name
    // =========================================================================

    [Fact]
    public void GetCategoryNameMap_ReturnsExpectedNames()
    {
        using var workspace = new TestWorkspace();
        var (db, sources, _, _) = CreateStack(workspace);

        var sourceId = sources.Create("YGEGE", "http://localhost:9117/api", "key", "query");
        InsertSourceCategory(db, sourceId, 2000, "Movies");
        InsertSourceCategory(db, sourceId, 100315, "FLAC");

        var map = sources.GetCategoryNameMap(sourceId);

        Assert.Equal(2, map.Count);
        Assert.Equal("Movies", map[2000]);
        Assert.Equal("FLAC", map[100315]);
    }

    [Fact]
    public void GetCategoryNameMap_EmptyForSourceWithNoCategories()
    {
        using var workspace = new TestWorkspace();
        var (db, sources, _, _) = CreateStack(workspace);

        var sourceId = sources.Create("Empty", "http://localhost:9117/api", "key", "query");

        var map = sources.GetCategoryNameMap(sourceId);

        Assert.Empty(map);
    }

    [Fact]
    public void GetCategoryNameMap_IsolatesPerSource()
    {
        using var workspace = new TestWorkspace();
        var (db, sources, _, _) = CreateStack(workspace);

        var s1 = sources.Create("S1", "http://s1/api", "k", "query");
        var s2 = sources.Create("S2", "http://s2/api", "k", "query");
        InsertSourceCategory(db, s1, 2000, "Movies");
        InsertSourceCategory(db, s2, 5000, "TV");

        var mapS1 = sources.GetCategoryNameMap(s1);
        var mapS2 = sources.GetCategoryNameMap(s2);

        Assert.Single(mapS1);
        Assert.True(mapS1.ContainsKey(2000));
        Assert.Single(mapS2);
        Assert.True(mapS2.ContainsKey(5000));
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static (Db db, SourceRepository sources, ReleaseRepository releases, SourcesController controller)
        CreateStack(TestWorkspace workspace)
    {
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sources = new SourceRepository(db, new PassthroughProtectionService());
        var releases = new ReleaseRepository(db, new TitleParser(), new UnifiedCategoryResolver());
        var activity = new ActivityRepository(db, new BadgeSignal());

        var controller = new SourcesController(
            torznab: null!,
            sources: sources,
            providers: null!,
            releases: releases,
            activity: activity,
            caps: null!,
            backupCoordinator: null!,
            syncOrchestration: null!,
            log: NullLogger<SourcesController>.Instance);

        return (db, sources, releases, controller);
    }

    private static void InsertSourceCategory(Db db, long sourceId, int catId, string name)
    {
        using var conn = db.Open();
        conn.Execute(
            """
            INSERT INTO source_categories(source_id, cat_id, name, is_sub)
            VALUES (@sid, @cid, @name, 0);
            """,
            new { sid = sourceId, cid = catId, name });
    }

    private static void InsertRelease(
        Db db,
        long sourceId,
        string guid,
        int categoryId,
        long publishedAtTs,
        string title,
        long sizeBytes = 0,
        int? seeders = null,
        int? stdCategoryId = null,
        string? categoryIds = null)
    {
        using var conn = db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        conn.Execute(
            """
            INSERT INTO releases(source_id, guid, title, published_at_ts, size_bytes,
                                 category_id, std_category_id, category_ids, seeders, created_at_ts)
            VALUES (@sid, @guid, @title, @pub, @size, @cat, @stdCat, @catIds, @seeders, @now);
            """,
            new { sid = sourceId, guid, title, pub = publishedAtTs, size = sizeBytes,
                  cat = categoryId, stdCat = stdCategoryId, catIds = categoryIds, seeders, now });
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

    private sealed class PassthroughProtectionService : IApiKeyProtectionService
    {
        public string Protect(string plainText) => plainText;
        public string Unprotect(string protectedText) => protectedText;
        public bool TryUnprotect(string protectedText, out string plainText)
        {
            plainText = protectedText;
            return true;
        }
        public bool IsProtected(string value) => false;
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
            catch { }
        }
    }
}
