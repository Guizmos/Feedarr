using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Titles;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class PosterStatsRefreshWorkerTests
{
    [Fact]
    public void RunRefreshCycle_Skips_WhenWatermarkUnchanged()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();
        SeedSingleRelease(db, releaseCreatedAtTs: 1_700_000_000);

        var repository = CreateRepository(db);
        var worker = CreateWorker(repository);

        var first = worker.RunRefreshCycle(CancellationToken.None);
        var second = worker.RunRefreshCycle(CancellationToken.None);

        Assert.Equal(PosterStatsRefreshWorker.PosterStatsRefreshCycleResult.Refreshed, first);
        Assert.Equal(PosterStatsRefreshWorker.PosterStatsRefreshCycleResult.SkippedUnchanged, second);
    }

    [Fact]
    public void RunRefreshCycle_Refreshes_WhenWatermarkChanges()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();
        var releaseId = SeedSingleRelease(db, releaseCreatedAtTs: 1_700_000_000);

        var repository = CreateRepository(db);
        var worker = CreateWorker(repository);

        _ = worker.RunRefreshCycle(CancellationToken.None);
        repository.UpdatePosterAttemptFailure(releaseId, "tmdb", "123", "fr", "w500", "timeout");
        var afterChange = worker.RunRefreshCycle(CancellationToken.None);

        Assert.Equal(PosterStatsRefreshWorker.PosterStatsRefreshCycleResult.Refreshed, afterChange);
    }

    private static PosterStatsRefreshWorker CreateWorker(ReleaseRepository releases)
        => new(
            releases,
            OptionsFactory.Create(new AppOptions { PosterStatsRefreshSeconds = 60 }),
            NullLogger<PosterStatsRefreshWorker>.Instance);

    private static ReleaseRepository CreateRepository(Db db)
        => new(db, new TitleParser(), new UnifiedCategoryResolver(), NullLogger<ReleaseRepository>.Instance);

    private static Db CreateDb(TestWorkspace workspace)
        => new(OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        }));

    private static long SeedSingleRelease(Db db, long releaseCreatedAtTs)
    {
        using var conn = db.Open();
        var now = releaseCreatedAtTs;
        conn.Execute(
            """
            INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
            VALUES ('Source A', 1, 'https://example.test', 'k', 'query', @ts, @ts);
            """,
            new { ts = now });

        var sourceId = conn.ExecuteScalar<long>("SELECT id FROM sources LIMIT 1;");
        conn.Execute(
            """
            INSERT INTO releases(source_id, guid, title, published_at_ts, created_at_ts)
            VALUES (@sid, 'guid-1', 'Release 1', @published, @created);
            """,
            new
            {
                sid = sourceId,
                published = now,
                created = now
            });

        return conn.ExecuteScalar<long>("SELECT id FROM releases LIMIT 1;");
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            RootDir = Path.Combine(Path.GetTempPath(), "feedarr-stats-refresh-tests", Guid.NewGuid().ToString("N"));
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
