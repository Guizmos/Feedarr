using System.Net;
using System.Text;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Torznab;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class SyncOrchestrationServiceCancellationTests
{
    [Fact]
    public async Task ExecuteManualSyncAsync_StopsWhenRequestIsCancelled()
    {
        using var workspace = new TestWorkspace();
        var options = OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        });

        var db = new Db(options);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var protection = new PassthroughProtectionService();
        var settings = new SettingsRepository(db, protection, NullLogger<SettingsRepository>.Instance);
        var sources = new SourceRepository(db, protection);
        var releases = new ReleaseRepository(db, new TitleParser(), new UnifiedCategoryResolver());
        var activity = new ActivityRepository(db, new BadgeSignal());
        var sourceId = sources.Create("Test Source", "http://localhost:9117/api", "secret", "query");
        var source = sources.Get(sourceId)!;

        var handler = new BlockingTorznabHandler();
        var torznab = new TorznabClient(
            new HttpClient(handler),
            new TorznabRssParser(),
            NullLogger<TorznabClient>.Instance);

        using var appLifetime = new TestHostApplicationLifetime();
        var service = new SyncOrchestrationService(
            torznab,
            sources,
            releases,
            activity,
            settings,
            new NoOpPosterFetchQueue(),
            new PosterFetchJobFactory(releases),
            new UnifiedCategoryResolver(),
            null!,
            options,
            appLifetime,
            NullLogger<SyncOrchestrationService>.Instance);

        using var requestCts = new CancellationTokenSource();
        var syncTask = service.ExecuteManualSyncAsync(source, rssOnly: false, requestCts.Token);

        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        requestCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await syncTask);
    }

    // -------------------------------------------------------------------------
    // Fix 4: silent catch → logged warning when settings read fails
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteManualSyncAsync_WhenSettingsReadFails_LogsWarning()
    {
        using var workspace = new TestWorkspace();

        // Main DB with migrations (sources, releases, activity)
        var mainOptions = OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        });
        var db = new Db(mainOptions);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        // Unmigrated DB for settings — its "settings" table does not exist → throws
        var settingsDbOptions = OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "settings-unmigrated.db"
        });
        var settingsDb = new Db(settingsDbOptions);

        var protection = new PassthroughProtectionService();
        var settings = new SettingsRepository(settingsDb, protection, NullLogger<SettingsRepository>.Instance);
        var sources = new SourceRepository(db, protection);
        var releases = new ReleaseRepository(db, new TitleParser(), new UnifiedCategoryResolver());
        var activity = new ActivityRepository(db, new BadgeSignal());

        var sourceId = sources.Create("WarningTest", "http://localhost:9117/api", "key", "query");
        var source = sources.Get(sourceId)!;

        var logger = new CapturingLogger<SyncOrchestrationService>();

        // Torznab returns HTTP 500 so sync fails fast (retention is never called)
        var service = new SyncOrchestrationService(
            new TorznabClient(
                new HttpClient(new ErrorTorznabHandler()),
                new TorznabRssParser(),
                NullLogger<TorznabClient>.Instance),
            sources,
            releases,
            activity,
            settings,
            new NoOpPosterFetchQueue(),
            new PosterFetchJobFactory(releases),
            new UnifiedCategoryResolver(),
            null!,
            mainOptions,
            new TestHostApplicationLifetime(),
            logger);

        // Should not throw — outer catch handles the Torznab error gracefully
        var result = await service.ExecuteManualSyncAsync(source, rssOnly: false, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.True(
            logger.HasWarning("Failed to read sync settings"),
            "Expected a Warning log about failed settings read");
    }

    /// <summary>Returns HTTP 500 immediately so sync fails fast.</summary>
    private sealed class ErrorTorznabHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("error", Encoding.UTF8, "text/plain")
            });
    }

    /// <summary>Captures log entries for assertion.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public bool HasWarning(string messageFragment) =>
            _entries.Any(e =>
                e.Level == LogLevel.Warning &&
                e.Message.Contains(messageFragment, StringComparison.OrdinalIgnoreCase));

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        bool ILogger.IsEnabled(LogLevel logLevel) => true;

        void ILogger.Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class BlockingTorznabHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<rss />", Encoding.UTF8, "application/xml")
            };
        }
    }

    private sealed class NoOpPosterFetchQueue : IPosterFetchQueue
    {
        public bool TryEnqueue(PosterFetchJob job) => true;
        public ValueTask<PosterFetchJob> DequeueAsync(CancellationToken ct) => ValueTask.FromCanceled<PosterFetchJob>(ct);
        public int ClearPending() => 0;
        public int Count => 0;
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
