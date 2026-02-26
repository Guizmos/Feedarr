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
    public void Top_WhenUnifiedCategoryExists_UsesItForCategoryGrouping()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = InsertSource(db, "source-films");
        InsertSourceCategory(db, sourceId, 2000, "Movies", unifiedKey: null, unifiedLabel: null);
        InsertRelease(
            db,
            sourceId,
            guid: "guid-film-1",
            categoryId: 2000,
            unifiedCategory: "Film",
            seeders: 90,
            categoryIds: "2000");

        var controller = CreateController(db);
        var action = controller.Top(sourceId: null, limit: 5, sortBy: "seeders");
        var ok = Assert.IsType<OkObjectResult>(action);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var byCategory = doc.RootElement.GetProperty("byCategory");
        Assert.True(byCategory.TryGetProperty("films", out var films));
        Assert.True(films.GetArrayLength() >= 1);
    }

    [Fact]
    public void Top_WhenUnifiedCategoryIsEmission_ReturnsCanonicalEmissionsGroup()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = InsertSource(db, "source-emissions");
        InsertSourceCategory(db, sourceId, 5000, "TV", unifiedKey: "shows", unifiedLabel: "Emissions");
        InsertRelease(
            db,
            sourceId,
            guid: "guid-show-1",
            categoryId: 5000,
            unifiedCategory: "Emission",
            seeders: 80,
            categoryIds: "5000");

        var controller = CreateController(db);
        var action = controller.Top(sourceId: null, limit: 5, sortBy: "seeders");
        var ok = Assert.IsType<OkObjectResult>(action);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var byCategory = doc.RootElement.GetProperty("byCategory");
        Assert.True(byCategory.TryGetProperty("emissions", out var emissions));
        Assert.True(emissions.GetArrayLength() >= 1);
    }

    [Fact]
    public void Top_WhenReleaseHasMultiCategoryIds_StillReturnsAnimeGroup()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = InsertSource(db, "source-anime");
        InsertSourceCategory(db, sourceId, 5000, "TV", unifiedKey: null, unifiedLabel: null);
        InsertRelease(
            db,
            sourceId,
            guid: "guid-anime-1",
            categoryId: 5000,
            unifiedCategory: "Anime",
            seeders: 70,
            categoryIds: "5000,5070");

        var controller = CreateController(db);
        var action = controller.Top(sourceId: null, limit: 5, sortBy: "seeders");
        var ok = Assert.IsType<OkObjectResult>(action);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var byCategory = doc.RootElement.GetProperty("byCategory");
        Assert.True(byCategory.TryGetProperty("anime", out var anime));
        Assert.True(anime.GetArrayLength() >= 1);
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
        string categoryIds)
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
                title = $"Release {guid}",
                publishedAt = now,
                catId = categoryId,
                unified = unifiedCategory,
                catIds = categoryIds,
                seeders,
                createdAt = now
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
