using System.Net;
using System.Text;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Sync;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Torznab;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class SyncOrchestrationServiceConcurrencyTests
{
    [Fact]
    public async Task ExecuteSourcesDetailedAsync_BoundsSourceConcurrency()
    {
        using var context = CreateContext(new BlockingSyncExecutor(threshold: 2), maxConcurrency: 2, sourceCount: 4);
        var executor = (BlockingSyncExecutor)context.Executor;

        var runTask = context.Service.ExecuteSourcesDetailedAsync(
            context.Sources,
            new AutoSyncPolicy(),
            rssOnly: false,
            CancellationToken.None);

        await executor.ThresholdReached.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, executor.StartedCount);
        Assert.Equal(2, executor.MaxInFlight);

        executor.ReleaseAll();

        var result = await runTask;

        Assert.Equal(4, result.TotalSources);
        Assert.Equal(4, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
        Assert.Equal(2, result.MaxConcurrency);
        Assert.All(result.Sources, source => Assert.True(source.Success));
        Assert.True(executor.MaxInFlight <= 2, $"Observed max in flight {executor.MaxInFlight}");
    }

    [Fact]
    public async Task ExecuteSourcesDetailedAsync_ContinuesWhenOneSourceThrows()
    {
        using var context = CreateContext(new FailingSyncExecutor("Source 2"), maxConcurrency: 2, sourceCount: 3);

        var result = await context.Service.ExecuteSourcesDetailedAsync(
            context.Sources,
            new AutoSyncPolicy(),
            rssOnly: false,
            CancellationToken.None);

        Assert.Equal(3, result.TotalSources);
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);

        var failed = Assert.Single(result.Sources.Where(source => !source.Success));
        Assert.Equal(nameof(InvalidOperationException), failed.ErrorType);
        Assert.Contains("boom", failed.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var succeededIds = result.Sources
            .Where(source => source.Success)
            .Select(source => source.SourceId)
            .OrderBy(id => id)
            .ToArray();
        var expectedSucceededIds = context.Sources
            .Where(source => !string.Equals(source.Name, "Source 2", StringComparison.Ordinal))
            .Select(source => source.Id)
            .OrderBy(id => id)
            .ToArray();

        Assert.Equal(expectedSucceededIds, succeededIds);
    }

    [Fact]
    public async Task ExecuteSourcesDetailedAsync_UsesPersistedMaintenanceConcurrencyOverride()
    {
        using var context = CreateContext(
            new BlockingSyncExecutor(threshold: 1),
            maxConcurrency: 4,
            sourceCount: 3,
            maintenanceSettings: new MaintenanceSettings
            {
                SyncSourcesMaxConcurrency = 1
            });
        var executor = (BlockingSyncExecutor)context.Executor;

        var runTask = context.Service.ExecuteSourcesDetailedAsync(
            context.Sources,
            new AutoSyncPolicy(),
            rssOnly: false,
            CancellationToken.None);

        await executor.ThresholdReached.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(150);

        Assert.Equal(1, executor.StartedCount);
        Assert.Equal(1, executor.MaxInFlight);

        executor.ReleaseAll();

        var result = await runTask;
        Assert.Equal(1, result.MaxConcurrency);
    }

    private static TestContext CreateContext(
        ISyncExecutor executor,
        int maxConcurrency,
        int sourceCount,
        MaintenanceSettings? maintenanceSettings = null)
    {
        var workspace = new TestWorkspace();
        var options = OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db",
            SyncSourcesMaxConcurrency = maxConcurrency
        });

        var db = new Db(options);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var protection = new PassthroughProtectionService();
        var settings = new SettingsRepository(db, protection, NullLogger<SettingsRepository>.Instance);
        var sources = new SourceRepository(db, protection);
        var releases = new ReleaseRepository(db, new TitleParser(), new UnifiedCategoryResolver());
        var activity = new ActivityRepository(db, new BadgeSignal());
        var providerStats = new ProviderStatsService(new StatsRepository(db, new MemoryCache(new MemoryCacheOptions())));
        var retention = new RetentionService(releases, null!, new PosterFileStore(), NullLogger<RetentionService>.Instance);

        if (maintenanceSettings is not null)
            settings.SaveMaintenance(maintenanceSettings);

        var createdSources = Enumerable.Range(1, sourceCount)
            .Select(index => sources.Get(sources.Create($"Source {index}", $"http://localhost:{9117 + index}/api", $"secret-{index}", "query"))!)
            .ToList();

        var service = new SyncOrchestrationService(
            new TorznabClient(
                new HttpClient(new EmptyTorznabHandler()),
                new TorznabRssParser(),
                NullLogger<TorznabClient>.Instance),
            sources,
            releases,
            activity,
            settings,
            new NoOpPosterFetchQueue(),
            new PosterFetchJobFactory(releases),
            new UnifiedCategoryResolver(),
            retention,
            providerStats,
            options,
            new TestHostApplicationLifetime(),
            NullLogger<SyncOrchestrationService>.Instance,
            new TestSyncPlanBuilder(),
            executor);

        return new TestContext(workspace, service, createdSources, executor);
    }

    private sealed class TestContext : IDisposable
    {
        private readonly TestWorkspace _workspace;

        public TestContext(TestWorkspace workspace, SyncOrchestrationService service, List<Source> sources, ISyncExecutor executor)
        {
            _workspace = workspace;
            Service = service;
            Sources = sources;
            Executor = executor;
        }

        public SyncOrchestrationService Service { get; }
        public IReadOnlyList<Source> Sources { get; }
        public ISyncExecutor Executor { get; }

        public void Dispose() => _workspace.Dispose();
    }

    private sealed class TestSyncPlanBuilder : ISyncPlanBuilder
    {
        public SyncPlan Build(SyncPlanInput input, SyncPolicy policy)
        {
            return new SyncPlan(
                input,
                new FetchPlan(
                    input.Settings.PerCategoryLimit,
                    input.Settings.RssOnly,
                    input.Settings.EnableCategoryFallback,
                    input.Settings.AllowSearchInitial),
                new FilterPlan(
                    input.PersistedCategoryIds,
                    input.SelectedCategoryIds,
                    input.MappedCategoryIds,
                    input.UnmappedCategoryIds,
                    [],
                    "test",
                    new Dictionary<int, (string key, string label)>(input.CategoryMap)),
                new DbPlan(
                    input.Settings.DefaultSeen,
                    input.Settings.GlobalLimit),
                new PosterPlan(
                    policy.PosterSelectionMode,
                    input.LastSyncAt,
                    ForceRefresh: false),
                new TelemetryPlan(
                    input.CorrelationId,
                    policy.LogPrefix,
                    input.TriggerReason,
                    policy.RecordIndexerQuery,
                    policy.RecordPerSourceSyncJob,
                    policy.EmitCategoryDebugActivity));
        }
    }

    private sealed class BlockingSyncExecutor : ISyncExecutor
    {
        private readonly int _threshold;
        private readonly TaskCompletionSource<bool> _thresholdReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startedCount;
        private int _inFlight;
        private int _maxInFlight;

        public BlockingSyncExecutor(int threshold) => _threshold = threshold;

        public Task ThresholdReached => _thresholdReached.Task;
        public int StartedCount => Volatile.Read(ref _startedCount);
        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public void ReleaseAll() => _releaseGate.TrySetResult(true);

        public async Task<SyncExecutionResult> ExecuteAsync(SyncPlan plan, CancellationToken ct)
        {
            var inFlight = Interlocked.Increment(ref _inFlight);
            UpdateMaxInFlight(inFlight);
            var started = Interlocked.Increment(ref _startedCount);
            if (started >= _threshold)
                _thresholdReached.TrySetResult(true);

            try
            {
                await _releaseGate.Task.WaitAsync(ct);
                return Success(plan.Input.Source);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private void UpdateMaxInFlight(int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxInFlight);
                if (candidate <= current)
                    return;

                if (Interlocked.CompareExchange(ref _maxInFlight, candidate, current) == current)
                    return;
            }
        }
    }

    private sealed class FailingSyncExecutor : ISyncExecutor
    {
        private readonly string _failureSourceName;

        public FailingSyncExecutor(string failureSourceName) => _failureSourceName = failureSourceName;

        public Task<SyncExecutionResult> ExecuteAsync(SyncPlan plan, CancellationToken ct)
        {
            if (string.Equals(plan.Input.Source.Name, _failureSourceName, StringComparison.Ordinal))
                throw new InvalidOperationException("boom from test executor");

            return Task.FromResult(Success(plan.Input.Source));
        }
    }

    private static SyncExecutionResult Success(Source source)
    {
        return new SyncExecutionResult(
            Ok: true,
            SourceId: source.Id,
            SourceName: source.Name,
            CorrelationId: Guid.NewGuid().ToString("N"),
            UsedMode: "rss",
            SyncMode: "rss",
            ItemsCount: 1,
            InsertedNew: 1,
            Error: null,
            ElapsedMs: 1,
            PosterRequested: 0,
            PosterEnqueued: 0,
            PosterCoalesced: 0,
            PosterTimedOut: 0);
    }

    private sealed class EmptyTorznabHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<rss version=\"2.0\"><channel /></rss>", Encoding.UTF8, "application/xml")
            });
        }
    }

    private sealed class NoOpPosterFetchQueue : IPosterFetchQueue
    {
        public ValueTask<PosterFetchEnqueueResult> EnqueueAsync(PosterFetchJob job, CancellationToken ct, TimeSpan timeout)
            => ValueTask.FromResult(new PosterFetchEnqueueResult(PosterFetchEnqueueStatus.Enqueued));

        public ValueTask<PosterFetchJob> DequeueAsync(CancellationToken ct)
            => ValueTask.FromCanceled<PosterFetchJob>(ct);

        public void RecordRetry()
        {
        }

        public PosterFetchJob? Complete(PosterFetchJob job, PosterFetchProcessResult result) => null;

        public int ClearPending() => 0;

        public int Count => 0;

        public PosterFetchQueueSnapshot GetSnapshot()
            => new(0, 0, false, null, null, null, null, 0, 0, 0, 0, 0, 0, 0);
    }

    private sealed class PassthroughProtectionService : IApiKeyProtectionService
    {
        public string Protect(string plainText) => plainText;
        public string Unprotect(string protectedText) => protectedText;

        public bool TryUnprotect(string protectedText, out string? plainText)
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
