using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Torznab;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class ReleaseRepositoryManualTitleOverrideTests
{
    [Fact]
    public void UpsertMany_DoesNotOverwriteManualRename()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        long sourceId;
        using (var conn = db.Open())
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            conn.Execute(
                """
                INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
                VALUES(@name, 1, @url, @apiKey, 'query', @ts, @ts);
                """,
                new
                {
                    name = "Test Source",
                    url = "http://localhost:9117/torznab",
                    apiKey = "secret",
                    ts = now
                });
            sourceId = conn.ExecuteScalar<long>("SELECT id FROM sources LIMIT 1;");
        }

        var repository = new ReleaseRepository(db, new TitleParser(), new UnifiedCategoryResolver());
        var originalTitle = "Goldorak (2013) iNTEGRALE REEDITION MULTi VFF 1080p DVDRip AC3 2.0 x264-GuS2SuG";
        var item = new TorznabItem
        {
            Guid = "guid-goldorak-1",
            Title = originalTitle,
            CategoryId = 2000,
            CategoryIds = new List<int> { 2000 }
        };

        repository.UpsertMany(sourceId, "TEST", new[] { item });

        long releaseId;
        using (var conn = db.Open())
        {
            releaseId = conn.ExecuteScalar<long>(
                "SELECT id FROM releases WHERE source_id = @sourceId AND guid = @guid;",
                new { sourceId, guid = item.Guid });
        }

        repository.RenameAndRebindEntity(releaseId, "Goldorak");
        repository.UpsertMany(sourceId, "TEST", new[] { item });

        using (var conn = db.Open())
        {
            var row = conn.QuerySingle<(string title, string titleClean, int titleManualOverride)>(
                """
                SELECT
                  title as title,
                  title_clean as titleClean,
                  title_manual_override as titleManualOverride
                FROM releases
                WHERE id = @id;
                """,
                new { id = releaseId });

            Assert.Equal("Goldorak", row.title);
            Assert.Equal("Goldorak", row.titleClean);
            Assert.Equal(1, row.titleManualOverride);
        }
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
