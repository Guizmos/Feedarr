using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Security;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class PosterFetchWorkerPoolTests
{
    [Fact(Timeout = 30000)]
    public async Task WorkerPool_WithTwoWorkers_ProcessesTwoJobsConcurrently()
    {
        using var ctx = new WorkerPoolContext(posterWorkers: 2);

        await ctx.Queue.EnqueueAsync(CreateJob(1), CancellationToken.None, PosterFetchQueue.DefaultEnqueueTimeout);
        await ctx.Queue.EnqueueAsync(CreateJob(2), CancellationToken.None, PosterFetchQueue.DefaultEnqueueTimeout);

        await ctx.Pool.StartAsync(CancellationToken.None);
        try
        {
            await ctx.Processor.WaitForConcurrentStartAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(2, ctx.Processor.MaxInFlight);

            ctx.Processor.ReleaseAll();
            await ctx.Processor.WaitForProcessedAsync(2);
        }
        finally
        {
            ctx.Processor.ReleaseAll();
            await ctx.Pool.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WorkerPool_WithOneWorker_ProcessesJobsSequentially()
    {
        using var ctx = new WorkerPoolContext(posterWorkers: 1);

        await ctx.Queue.EnqueueAsync(CreateJob(1), CancellationToken.None, PosterFetchQueue.DefaultEnqueueTimeout);
        await ctx.Queue.EnqueueAsync(CreateJob(2), CancellationToken.None, PosterFetchQueue.DefaultEnqueueTimeout);

        await ctx.Pool.StartAsync(CancellationToken.None);
        try
        {
            await ctx.Processor.WaitForAttemptedAsync(TimeSpan.FromSeconds(15));

            await Task.Delay(150);
            Assert.Equal(1, ctx.Processor.MaxInFlight);

            ctx.Processor.ReleaseAll();
            await ctx.Processor.WaitForProcessedAsync(2);
        }
        finally
        {
            ctx.Processor.ReleaseAll();
            await ctx.Pool.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WorkerPool_WhenJobThrows_ContinuesProcessingNextJobs()
    {
        using var ctx = new WorkerPoolContext(posterWorkers: 2, failFirstJob: true);

        await ctx.Queue.EnqueueAsync(CreateJob(1), CancellationToken.None, PosterFetchQueue.DefaultEnqueueTimeout);
        await ctx.Queue.EnqueueAsync(CreateJob(2), CancellationToken.None, PosterFetchQueue.DefaultEnqueueTimeout);

        await ctx.Pool.StartAsync(CancellationToken.None);
        try
        {
            ctx.Processor.ReleaseAll();
            await ctx.Processor.WaitForProcessedAsync(2);
        }
        finally
        {
            ctx.Processor.ReleaseAll();
            await ctx.Pool.StopAsync(CancellationToken.None);
        }

        Assert.Contains(1L, ctx.Processor.AttemptedItemIds);
        Assert.Contains(2L, ctx.Processor.AttemptedItemIds);
        Assert.Contains(2L, ctx.Processor.SuccessfulItemIds);
    }

    private static PosterFetchJob CreateJob(long itemId)
        => new(
            ItemId: itemId,
            Title: $"Title {itemId}",
            Year: 2024,
            Category: UnifiedCategory.Film,
            ForceRefresh: false,
            AttemptCount: 0,
            EntityId: null,
            RetroLogFile: null);

    private sealed class WorkerPoolContext : IDisposable
    {
        private readonly TestWorkspace _workspace;

        public WorkerPoolContext(int posterWorkers, bool failFirstJob = false)
        {
            _workspace = new TestWorkspace();
            var options = OptionsFactory.Create(new AppOptions
            {
                DataDir = _workspace.DataDir,
                DbFileName = "feedarr.db"
            });

            var db = new Db(options);
            new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

            var settings = new SettingsRepository(db, new PassthroughProtectionService(), NullLogger<SettingsRepository>.Instance);
            settings.SaveMaintenance(new MaintenanceSettings
            {
                PosterWorkers = posterWorkers
            });

            Queue = new PosterFetchQueue(NullLogger<PosterFetchQueue>.Instance);
            Processor = new FakePosterFetchJobProcessor(failFirstJob);
            Pool = new PosterFetchWorkerPool(
                NullLogger<PosterFetchWorkerPool>.Instance,
                Queue,
                Processor,
                settings);
        }

        public PosterFetchQueue Queue { get; }
        public FakePosterFetchJobProcessor Processor { get; }
        public PosterFetchWorkerPool Pool { get; }

        public void Dispose()
        {
            _workspace.Dispose();
        }
    }

    private sealed class FakePosterFetchJobProcessor : IPosterFetchJobProcessor
    {
        private readonly bool _failFirstJob;
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _processed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CountdownEvent _concurrentStart = new(2);
        private int _inFlight;
        private int _processedCount;
        private int _maxInFlight;
        private int _hasFailed;

        public FakePosterFetchJobProcessor(bool failFirstJob)
        {
            _failFirstJob = failFirstJob;
        }

        public List<long> AttemptedItemIds { get; } = [];
        public List<long> SuccessfulItemIds { get; } = [];
        public int AttemptedCount
        {
            get
            {
                lock (AttemptedItemIds)
                    return AttemptedItemIds.Count;
            }
        }

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public async Task<PosterFetchProcessResult> ProcessJobAsync(PosterFetchJob job, int workerId, CancellationToken stoppingToken)
        {
            lock (AttemptedItemIds)
            {
                AttemptedItemIds.Add(job.ItemId);
                if (AttemptedItemIds.Count >= 1)
                    _attempted.TrySetResult(true);
            }

            if (_failFirstJob && Interlocked.CompareExchange(ref _hasFailed, 1, 0) == 0)
            {
                Interlocked.Increment(ref _processedCount);
                if (Volatile.Read(ref _processedCount) >= 2)
                    _processed.TrySetResult(true);
                throw new InvalidOperationException("boom");
            }

            var current = Interlocked.Increment(ref _inFlight);
            UpdateMax(current);
            _concurrentStart.Signal();

            try
            {
                await _release.Task.WaitAsync(stoppingToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
                lock (SuccessfulItemIds)
                    SuccessfulItemIds.Add(job.ItemId);

                Interlocked.Increment(ref _processedCount);
                if (Volatile.Read(ref _processedCount) >= 2)
                    _processed.TrySetResult(true);
            }

            return new PosterFetchProcessResult(true);
        }

        public void ReleaseAll()
            => _release.TrySetResult(true);

        public Task WaitForAttemptedAsync(TimeSpan timeout)
        {
            if (AttemptedCount >= 1)
                return Task.CompletedTask;

            return _attempted.Task.WaitAsync(timeout);
        }

        public Task WaitForConcurrentStartAsync(TimeSpan timeout)
        {
            if (Volatile.Read(ref _maxInFlight) >= 2)
                return Task.CompletedTask;

            if (_concurrentStart.Wait(timeout))
                return Task.CompletedTask;

            return Task.FromException(new TimeoutException("Timed out waiting for both worker jobs to start."));
        }

        public Task WaitForProcessedAsync(int expected)
        {
            if (Volatile.Read(ref _processedCount) >= expected)
                return Task.CompletedTask;

            return _processed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private void UpdateMax(int candidate)
        {
            while (true)
            {
                var snapshot = Volatile.Read(ref _maxInFlight);
                if (candidate <= snapshot)
                    return;

                if (Interlocked.CompareExchange(ref _maxInFlight, candidate, snapshot) == snapshot)
                    return;
            }
        }
    }

    private sealed class PassthroughProtectionService : IApiKeyProtectionService
    {
        public string Protect(string plainText) => plainText;
        public string Unprotect(string protectedText) => protectedText;
        public bool TryUnprotect(string protectedText, out string? plainText) { plainText = protectedText; return true; }
        public bool IsProtected(string value) => false;
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            RootDir = Path.Combine(Path.GetTempPath(), "feedarr-poster-worker-pool-tests", Guid.NewGuid().ToString("N"));
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
