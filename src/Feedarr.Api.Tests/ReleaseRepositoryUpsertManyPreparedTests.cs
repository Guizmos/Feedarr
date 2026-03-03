using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Torznab;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class ReleaseRepositoryUpsertManyPreparedTests
{
    [Fact]
    public void UpsertMany_InsertsNewRows_AndReturnsInsertedCount()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = CreateSource(db);
        var repository = CreateRepository(db);

        var insertedNew = repository.UpsertMany(sourceId, "TEST", new[]
        {
            CreateItem("guid-1", "The Matrix 1999 1080p", 2000),
            CreateItem("guid-2", "Inception 2010 1080p", 2000),
        });

        using var conn = db.Open();
        var count = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM releases WHERE source_id = @sourceId;", new { sourceId });

        Assert.Equal(2, insertedNew);
        Assert.Equal(2, count);
    }

    [Fact]
    public void UpsertMany_UpdatesExistingRows_WithoutCountingAsInserted()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = CreateSource(db);
        var repository = CreateRepository(db);

        repository.UpsertMany(sourceId, "TEST", new[]
        {
            CreateItem("guid-update", "Interstellar 2014 1080p", 2000, seeders: 10, grabs: 1)
        });

        var insertedNew = repository.UpsertMany(sourceId, "TEST", new[]
        {
            CreateItem("guid-update", "Interstellar 2014 1080p", 2000, seeders: 42, grabs: 8)
        });

        using var conn = db.Open();
        var row = conn.QuerySingle<(int? seeders, int? grabs)>(
            """
            SELECT seeders as seeders, grabs as grabs
            FROM releases
            WHERE source_id = @sourceId AND guid = @guid;
            """,
            new { sourceId, guid = "guid-update" });

        Assert.Equal(0, insertedNew);
        Assert.Equal(42, row.seeders);
        Assert.Equal(8, row.grabs);
    }

    [Fact]
    public void UpsertMany_DoesNotOverwriteTitle_WhenManualOverrideIsEnabled()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = CreateSource(db);
        var repository = CreateRepository(db);

        repository.UpsertMany(sourceId, "TEST", new[]
        {
            CreateItem("guid-manual", "Original Auto Title 2024 1080p", 2000)
        });

        using (var conn = db.Open())
        {
            conn.Execute(
                """
                UPDATE releases
                SET title = @title,
                    title_clean = @titleClean,
                    title_manual_override = 1
                WHERE source_id = @sourceId AND guid = @guid;
                """,
                new
                {
                    title = "Manual",
                    titleClean = "Manual",
                    sourceId,
                    guid = "guid-manual"
                });
        }

        var insertedNew = repository.UpsertMany(sourceId, "TEST", new[]
        {
            CreateItem("guid-manual", "Updated Auto Title 2024 2160p", 2000, seeders: 25)
        });

        using var verifyConn = db.Open();
        var row = verifyConn.QuerySingle<(string title, string titleClean, int titleManualOverride, int? seeders)>(
            """
            SELECT
              title as title,
              title_clean as titleClean,
              title_manual_override as titleManualOverride,
              seeders as seeders
            FROM releases
            WHERE source_id = @sourceId AND guid = @guid;
            """,
            new { sourceId, guid = "guid-manual" });

        Assert.Equal(0, insertedNew);
        Assert.Equal("Manual", row.title);
        Assert.Equal("Manual", row.titleClean);
        Assert.Equal(1, row.titleManualOverride);
        Assert.Equal(25, row.seeders);
    }

    [Fact]
    public void UpsertMany_PopulatesEntityId_AfterInsert()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = CreateSource(db);
        var repository = CreateRepository(db);

        repository.UpsertMany(sourceId, "TEST", new[]
        {
            CreateItem("guid-entity", "Arrival 2016 1080p", 2000)
        });

        using var conn = db.Open();
        var row = conn.QuerySingle<(long? entityId, string? unifiedCategory, string? titleClean)>(
            """
            SELECT entity_id as entityId, unified_category as unifiedCategory, title_clean as titleClean
            FROM releases
            WHERE source_id = @sourceId AND guid = @guid;
            """,
            new { sourceId, guid = "guid-entity" });

        Assert.NotNull(row.entityId);
        Assert.True(row.entityId > 0);
        Assert.False(string.IsNullOrWhiteSpace(row.unifiedCategory));
        Assert.False(string.IsNullOrWhiteSpace(row.titleClean));
    }

    private static ReleaseRepository CreateRepository(Db db)
        => new(db, new TitleParser(), new UnifiedCategoryResolver(), NullLogger<ReleaseRepository>.Instance);

    private static long CreateSource(Db db)
    {
        using var conn = db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        conn.Execute(
            """
            INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
            VALUES ('Test Source', 1, 'http://localhost:9117/torznab', 'secret', 'query', @now, @now);
            """,
            new { now });

        return conn.ExecuteScalar<long>("SELECT id FROM sources ORDER BY id DESC LIMIT 1;");
    }

    private static TorznabItem CreateItem(string guid, string title, int categoryId, int? seeders = null, int? grabs = null)
        => new()
        {
            Guid = guid,
            Title = title,
            CategoryId = categoryId,
            CategoryIds = new List<int> { categoryId },
            Seeders = seeders,
            Grabs = grabs
        };

    private static Db CreateDb(TestWorkspace workspace)
        => new(OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        }));

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
