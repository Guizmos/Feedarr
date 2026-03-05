using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Titles;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class MissingPosterSweepWorkerTests
{
    [Fact]
    public async Task GetReleaseIdsMissingPosterAsync_RespectsLimit()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();
        using var conn = db.Open();
        var sourceId = InsertSource(conn, 1_700_000_000);

        InsertRelease(conn, sourceId, "r1", 1_700_000_001, 1_700_000_001, posterFile: null);
        InsertRelease(conn, sourceId, "r2", 1_700_000_002, 1_700_000_002, posterFile: null);
        InsertRelease(conn, sourceId, "r3", 1_700_000_003, 1_700_000_003, posterFile: null);
        InsertRelease(conn, sourceId, "r4", 1_700_000_004, 1_700_000_004, posterFile: "already.jpg");

        var repository = CreateRepository(db);
        var ids = await repository.GetReleaseIdsMissingPosterAsync(2, CancellationToken.None);

        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public async Task SweepOnceAsync_EnqueuesMissingPosters()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();
        using var conn = db.Open();
        var sourceId = InsertSource(conn, 1_700_000_000);
        var id1 = InsertRelease(conn, sourceId, "missing-1", 1_700_000_010, 1_700_000_010, posterFile: null);
        var id2 = InsertRelease(conn, sourceId, "missing-2", 1_700_000_011, 1_700_000_011, posterFile: null);

        var repository = CreateRepository(db);
        var queue = new FakePosterFetchQueue(new PosterFetchEnqueueBatchResult(2, 0, 0, 0));
        var worker = CreateWorker(repository, queue);

        var result = await worker.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(2, result.Found);
        Assert.Equal(2, result.Requested);
        Assert.Equal(2, result.Enqueued);
        Assert.Equal(1, queue.EnqueueManyCalls);
        Assert.Contains(id1, queue.LastJobIds);
        Assert.Contains(id2, queue.LastJobIds);
    }

    [Fact]
    public async Task SweepOnceAsync_WhenEnqueueTimesOut_CanRetryNextRun()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();
        using var conn = db.Open();
        var sourceId = InsertSource(conn, 1_700_000_000);
        _ = InsertRelease(conn, sourceId, "missing-1", 1_700_000_010, 1_700_000_010, posterFile: null);
        _ = InsertRelease(conn, sourceId, "missing-2", 1_700_000_011, 1_700_000_011, posterFile: null);

        var repository = CreateRepository(db);
        var queue = new FakePosterFetchQueue(new PosterFetchEnqueueBatchResult(0, 0, 2, 0));
        var worker = CreateWorker(repository, queue);

        var first = await worker.SweepOnceAsync(CancellationToken.None);
        var second = await worker.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(2, first.TimedOut);
        Assert.Equal(2, second.TimedOut);
        Assert.Equal(2, queue.EnqueueManyCalls);
    }

    private static MissingPosterSweepWorker CreateWorker(ReleaseRepository releases, IPosterFetchQueue queue)
        => new(
            releases,
            new PosterFetchJobFactory(releases),
            queue,
            OptionsFactory.Create(new AppOptions
            {
                MissingPosterSweepMinutes = 10,
                MissingPosterSweepBatchSize = 200
            }),
            NullLogger<MissingPosterSweepWorker>.Instance);

    private static ReleaseRepository CreateRepository(Db db)
        => new(db, new TitleParser(), new UnifiedCategoryResolver(), NullLogger<ReleaseRepository>.Instance);

    private static Db CreateDb(TestWorkspace workspace)
        => new(OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        }));

    private static long InsertSource(SqliteConnection conn, long ts)
    {
        conn.Execute(
            """
            INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
            VALUES ('Source A', 1, 'https://example.test', 'k', 'query', @ts, @ts);
            """,
            new { ts });
        return conn.ExecuteScalar<long>("SELECT id FROM sources LIMIT 1;");
    }

    private static long InsertRelease(SqliteConnection conn, long sourceId, string guid, long publishedAt, long createdAt, string? posterFile)
    {
        conn.Execute(
            """
            INSERT INTO releases(source_id, guid, title, published_at_ts, created_at_ts, poster_file)
            VALUES (@sourceId, @guid, @title, @publishedAt, @createdAt, @posterFile);
            """,
            new
            {
                sourceId,
                guid,
                title = guid,
                publishedAt,
                createdAt,
                posterFile
            });

        return conn.ExecuteScalar<long>("SELECT id FROM releases WHERE guid = @guid;", new { guid });
    }

    private sealed class FakePosterFetchQueue : IPosterFetchQueue
    {
        private readonly PosterFetchEnqueueBatchResult _batchResult;

        public FakePosterFetchQueue(PosterFetchEnqueueBatchResult batchResult)
        {
            _batchResult = batchResult;
        }

        public int EnqueueManyCalls { get; private set; }
        public IReadOnlyList<long> LastJobIds { get; private set; } = Array.Empty<long>();

        public ValueTask<PosterFetchEnqueueResult> EnqueueAsync(PosterFetchJob job, CancellationToken ct, TimeSpan timeout)
            => new(new PosterFetchEnqueueResult(PosterFetchEnqueueStatus.Enqueued));

        public ValueTask<PosterFetchEnqueueBatchResult> EnqueueManyAsync(IReadOnlyList<PosterFetchJob> jobs, CancellationToken ct, TimeSpan timeout)
        {
            EnqueueManyCalls++;
            LastJobIds = jobs.Select(job => job.ItemId).ToArray();
            return new ValueTask<PosterFetchEnqueueBatchResult>(_batchResult);
        }

        public ValueTask<PosterFetchJob> DequeueAsync(CancellationToken ct)
            => throw new NotImplementedException();

        public void RecordRetry()
        {
        }

        public PosterFetchJob? Complete(PosterFetchJob job, PosterFetchProcessResult result)
            => null;

        public int ClearPending() => 0;

        public int Count => 0;

        public PosterFetchQueueSnapshot GetSnapshot()
            => new(0, 0, false, null, null, null, null, 0, 0, 0, 0, 0, 0, 0);
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            RootDir = Path.Combine(Path.GetTempPath(), "feedarr-missing-sweep-tests", Guid.NewGuid().ToString("N"));
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
