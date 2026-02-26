using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Torznab;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class ReleaseRepositorySourceScopedReclassifyTests
{
    [Fact]
    public void ReclassifySource_OnlyUpdatesTargetSource()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceRepository = new SourceRepository(db, new PassthroughProtectionService());
        var releaseRepository = new ReleaseRepository(db, new TitleParser(), new UnifiedCategoryResolver());

        var source1 = sourceRepository.Create("Source 1", "http://localhost:9117/s1", "key", "query");
        var source2 = sourceRepository.Create("Source 2", "http://localhost:9117/s2", "key", "query");

        sourceRepository.PatchCategoryMappings(source1, new[]
        {
            new SourceRepository.SourceCategoryMappingPatch { CatId = 5050, GroupKey = "films" }
        });
        sourceRepository.PatchCategoryMappings(source2, new[]
        {
            new SourceRepository.SourceCategoryMappingPatch { CatId = 5050, GroupKey = "films" }
        });

        releaseRepository.UpsertMany(
            source1,
            "Source 1",
            new[]
            {
                new TorznabItem
                {
                    Guid = "s1-guid",
                    Title = "Series A S01E01 1080p",
                    CategoryId = 5050,
                    CategoryIds = new List<int> { 5050 },
                    PublishedAtTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                }
            },
            categoryMap: sourceRepository.GetCategoryMappingMap(source1));

        releaseRepository.UpsertMany(
            source2,
            "Source 2",
            new[]
            {
                new TorznabItem
                {
                    Guid = "s2-guid",
                    Title = "Series B S01E01 1080p",
                    CategoryId = 5050,
                    CategoryIds = new List<int> { 5050 },
                    PublishedAtTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                }
            },
            categoryMap: sourceRepository.GetCategoryMappingMap(source2));

        sourceRepository.PatchCategoryMappings(source1, new[]
        {
            new SourceRepository.SourceCategoryMappingPatch { CatId = 5050, GroupKey = "series" }
        });

        var (processed, updated, markedRebind) = releaseRepository.ReprocessCategoriesForSource(source1);
        Assert.True(processed >= 1);
        Assert.True(updated >= 1);
        Assert.Equal(updated, markedRebind);

        using (var conn = db.Open())
        {
            var source1Category = conn.ExecuteScalar<string>(
                "SELECT unified_category FROM releases WHERE source_id = @sid LIMIT 1;",
                new { sid = source1 });
            var source1NeedsRebind = conn.ExecuteScalar<int>(
                "SELECT needs_rebind FROM releases WHERE source_id = @sid LIMIT 1;",
                new { sid = source1 });

            var source2Category = conn.ExecuteScalar<string>(
                "SELECT unified_category FROM releases WHERE source_id = @sid LIMIT 1;",
                new { sid = source2 });
            var source2NeedsRebind = conn.ExecuteScalar<int>(
                "SELECT needs_rebind FROM releases WHERE source_id = @sid LIMIT 1;",
                new { sid = source2 });

            Assert.Equal("Serie", source1Category);
            Assert.Equal(1, source1NeedsRebind);

            Assert.Equal("Film", source2Category);
            Assert.Equal(0, source2NeedsRebind);
        }

        var (_, rebound) = releaseRepository.RebindEntitiesForSource(source1, 200);
        Assert.True(rebound >= 1);

        using var finalConn = db.Open();
        var afterRebindSource1 = finalConn.ExecuteScalar<int>(
            "SELECT needs_rebind FROM releases WHERE source_id = @sid LIMIT 1;",
            new { sid = source1 });
        var afterRebindSource2 = finalConn.ExecuteScalar<int>(
            "SELECT needs_rebind FROM releases WHERE source_id = @sid LIMIT 1;",
            new { sid = source2 });

        Assert.Equal(0, afterRebindSource1);
        Assert.Equal(0, afterRebindSource2);
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
            catch
            {
            }
        }
    }
}
