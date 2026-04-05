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
    public void UpsertMany_WithEmptyItems_ReturnsZero_AndDoesNotInsertRows()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = CreateSource(db);
        var repository = CreateRepository(db);

        var insertedNew = repository.UpsertMany(sourceId, "TEST", Array.Empty<TorznabItem>());

        using var conn = db.Open();
        var count = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM releases WHERE source_id = @sourceId;", new { sourceId });

        Assert.Equal(0, insertedNew);
        Assert.Equal(0, count);
    }

    [Fact]
    public void UpsertMany_AppliesDefaultSeen_OnInsertedRows()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = CreateSource(db);
        var repository = CreateRepository(db);

        repository.UpsertMany(
            sourceId,
            "TEST",
            new[]
            {
                CreateItem("guid-seen", "Seen Flag 2024 1080p", 2000)
            },
            defaultSeen: 1);

        using var conn = db.Open();
        var seen = conn.ExecuteScalar<int>(
            "SELECT seen FROM releases WHERE source_id = @sourceId AND guid = @guid;",
            new { sourceId, guid = "guid-seen" });

        Assert.Equal(1, seen);
    }

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
    public void UpsertMany_PreservesExistingValues_WhenIncomingOptionalFieldsAreNull()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = CreateSource(db);
        var repository = CreateRepository(db);

        repository.UpsertMany(sourceId, "TEST", new[]
        {
            new TorznabItem
            {
                Guid = "guid-coalesce",
                Title = "Coalesce Test 2024 1080p",
                CategoryId = 2000,
                CategoryIds = new List<int> { 2000 },
                Seeders = 17,
                Leechers = 3,
                Grabs = 5,
                InfoHash = "hash-1",
                DownloadUrl = "https://example.test/dl-a",
                SizeBytes = 3_000_000_000,
                PublishedAtTs = 1_700_000_000
            }
        });

        repository.UpsertMany(sourceId, "TEST", new[]
        {
            new TorznabItem
            {
                Guid = "guid-coalesce",
                Title = "Coalesce Test 2024 1080p",
                CategoryId = null,
                CategoryIds = new List<int>(),
                Seeders = null,
                Leechers = null,
                Grabs = null,
                InfoHash = null,
                DownloadUrl = null,
                SizeBytes = null,
                PublishedAtTs = 1_700_000_100
            }
        });

        using var conn = db.Open();
        var row = conn.QuerySingle<(int? seeders, int? leechers, int? grabs, string? infoHash, string? downloadUrl, long? sizeBytes, int? categoryId)>(
            """
            SELECT
              seeders as seeders,
              leechers as leechers,
              grabs as grabs,
              info_hash as infoHash,
              download_url as downloadUrl,
              size_bytes as sizeBytes,
              category_id as categoryId
            FROM releases
            WHERE source_id = @sourceId AND guid = @guid;
            """,
            new { sourceId, guid = "guid-coalesce" });

        Assert.Equal(17, row.seeders);
        Assert.Equal(3, row.leechers);
        Assert.Equal(5, row.grabs);
        Assert.Equal("hash-1", row.infoHash);
        Assert.Equal("https://example.test/dl-a", row.downloadUrl);
        Assert.Equal(3_000_000_000, row.sizeBytes);
        Assert.Equal(2000, row.categoryId);
    }

    [Fact]
    public void UpsertMany_UsesInfoHashAsGuid_WhenGuidMatchesTitle()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = CreateSource(db);
        var repository = CreateRepository(db);

        var title = "Fallback Guid Title 2024 1080p";
        repository.UpsertMany(sourceId, "TEST", new[]
        {
            new TorznabItem
            {
                Guid = title,
                Title = title,
                InfoHash = "fallback-info-hash",
                CategoryId = 2000,
                CategoryIds = new List<int> { 2000 }
            }
        });

        using var conn = db.Open();
        var storedGuid = conn.ExecuteScalar<string>(
            "SELECT guid FROM releases WHERE source_id = @sourceId LIMIT 1;",
            new { sourceId });

        Assert.Equal("fallback-info-hash", storedGuid);
    }

    [Fact]
    public void UpsertMany_UsesDeterministicComputedGuid_WhenIdentifiersAreMissing()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = CreateSource(db);
        var repository = CreateRepository(db);
        var publishedAt = 1_700_000_123L;

        var item = new TorznabItem
        {
            Guid = "",
            Title = "",
            CategoryId = 2000,
            CategoryIds = new List<int> { 2000 },
            SizeBytes = 4_321,
            PublishedAtTs = publishedAt
        };

        var inserted1 = repository.UpsertMany(sourceId, "TEST", new[] { item });
        var inserted2 = repository.UpsertMany(sourceId, "TEST", new[] { item });

        using var conn = db.Open();
        var row = conn.QuerySingle<(string guid, int count)>(
            """
            SELECT guid as guid, COUNT(1) as count
            FROM releases
            WHERE source_id = @sourceId
            GROUP BY guid;
            """,
            new { sourceId });

        Assert.Equal(1, inserted1);
        Assert.Equal(0, inserted2);
        Assert.Equal(1, row.count);
        Assert.Contains("|4321|", row.guid, StringComparison.Ordinal);
    }

    [Fact]
    public void UpsertMany_HandlesMoreThan500Guids_WhenComputingInsertedCount()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = CreateSource(db);
        var repository = CreateRepository(db);

        var items = Enumerable.Range(1, 750)
            .Select(i => CreateItem($"guid-bulk-{i}", $"Bulk Title {i} 2024 1080p", 2000))
            .ToList();

        var insertedNew = repository.UpsertMany(sourceId, "TEST", items);

        using var conn = db.Open();
        var count = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM releases WHERE source_id = @sourceId;", new { sourceId });

        Assert.Equal(750, insertedNew);
        Assert.Equal(750, count);
    }

    [Fact]
    public void UpsertMany_DuplicateGuidInSameBatch_CountsSingleInsert()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = CreateSource(db);
        var repository = CreateRepository(db);

        var insertedNew = repository.UpsertMany(sourceId, "TEST", new[]
        {
            CreateItem("guid-dup-batch", "Duplicate Batch 2024 1080p", 2000, seeders: 10),
            CreateItem("guid-dup-batch", "Duplicate Batch 2024 2160p", 2000, seeders: 50)
        });

        using var conn = db.Open();
        var count = conn.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM releases WHERE source_id = @sourceId AND guid = @guid;",
            new { sourceId, guid = "guid-dup-batch" });
        var seeders = conn.ExecuteScalar<int?>(
            "SELECT seeders FROM releases WHERE source_id = @sourceId AND guid = @guid;",
            new { sourceId, guid = "guid-dup-batch" });

        Assert.Equal(1, insertedNew);
        Assert.Equal(1, count);
        Assert.Equal(50, seeders);
    }

    [Fact]
    public void UpsertMany_UsesCreatedAtOverride_ForInsertedRows()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = CreateSource(db);
        var repository = CreateRepository(db);
        const long overrideTs = 1_700_123_456;

        repository.UpsertMany(
            sourceId,
            "TEST",
            new[]
            {
                CreateItem("guid-created-override", "Created Override 2024 1080p", 2000)
            },
            createdAtTs: overrideTs);

        using var conn = db.Open();
        var createdAt = conn.ExecuteScalar<long>(
            "SELECT created_at_ts FROM releases WHERE source_id = @sourceId AND guid = @guid;",
            new { sourceId, guid = "guid-created-override" });

        Assert.Equal(overrideTs, createdAt);
    }

    [Fact]
    public void UpsertMany_UsesDownloadUrlAsGuid_WhenGuidMatchesTitleAndNoInfoHash()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = CreateSource(db);
        var repository = CreateRepository(db);

        var title = "Fallback Download Url 2024 1080p";
        repository.UpsertMany(sourceId, "TEST", new[]
        {
            new TorznabItem
            {
                Guid = title,
                Title = title,
                InfoHash = null,
                DownloadUrl = "https://example.test/fallback-download",
                CategoryId = 2000,
                CategoryIds = new List<int> { 2000 }
            }
        });

        using var conn = db.Open();
        var storedGuid = conn.ExecuteScalar<string>(
            "SELECT guid FROM releases WHERE source_id = @sourceId LIMIT 1;",
            new { sourceId });

        Assert.Equal("https://example.test/fallback-download", storedGuid);
    }

    [Fact]
    public void UpsertMany_ReupsertLargeExistingSet_ReturnsZero()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = CreateSource(db);
        var repository = CreateRepository(db);

        var items = Enumerable.Range(1, 620)
            .Select(i => CreateItem($"guid-existing-{i}", $"Existing {i} 2024 1080p", 2000, seeders: i))
            .ToList();

        repository.UpsertMany(sourceId, "TEST", items);
        var insertedNew = repository.UpsertMany(sourceId, "TEST", items);

        Assert.Equal(0, insertedNew);
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
