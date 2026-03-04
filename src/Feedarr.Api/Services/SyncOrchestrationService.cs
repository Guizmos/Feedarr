using System.Diagnostics;
using System.Collections.Concurrent;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Sync;
using Feedarr.Api.Services.Torznab;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Services;

public sealed record SyncOrchestrationResult(
    bool Ok,
    string? UsedMode,
    string? SyncMode,
    int ItemsCount,
    int InsertedNew,
    string? Error);

internal sealed record SourceSyncResult(
    long SourceId,
    string SourceName,
    DateTimeOffset StartedAtUtc,
    long DurationMs,
    bool Success,
    string? ErrorType,
    string? ErrorMessage,
    int PosterQueuePending);

internal sealed record SyncSourcesRunResult(
    int TotalSources,
    int SuccessCount,
    int FailureCount,
    long DurationMs,
    int MaxConcurrency,
    IReadOnlyList<SourceSyncResult> Sources);

public sealed class SyncOrchestrationService
{
    private readonly TorznabClient _torznab;
    private readonly SourceRepository _sources;
    private readonly ReleaseRepository _releases;
    private readonly ActivityRepository _activity;
    private readonly SettingsRepository _settings;
    private readonly IPosterFetchQueue _posterQueue;
    private readonly PosterFetchJobFactory _posterJobs;
    private readonly UnifiedCategoryResolver _resolver;
    private readonly RetentionService _retention;
    private readonly ProviderStatsService _providerStats;
    private readonly AppOptions _opts;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly ILogger<SyncOrchestrationService> _log;
    private readonly ISyncPlanBuilder _planBuilder;
    private readonly ISyncExecutor _executor;

    public SyncOrchestrationService(
        TorznabClient torznab,
        SourceRepository sources,
        ReleaseRepository releases,
        ActivityRepository activity,
        SettingsRepository settings,
        IPosterFetchQueue posterQueue,
        PosterFetchJobFactory posterJobs,
        UnifiedCategoryResolver resolver,
        RetentionService retention,
        ProviderStatsService providerStats,
        IOptions<AppOptions> opts,
        IHostApplicationLifetime appLifetime,
        ILogger<SyncOrchestrationService> log,
        ISyncPlanBuilder? planBuilder = null,
        ISyncExecutor? executor = null)
    {
        _torznab = torznab;
        _sources = sources;
        _releases = releases;
        _activity = activity;
        _settings = settings;
        _posterQueue = posterQueue;
        _posterJobs = posterJobs;
        _resolver = resolver;
        _retention = retention;
        _providerStats = providerStats;
        _opts = opts.Value;
        _appLifetime = appLifetime;
        _log = log;
        _planBuilder = planBuilder ?? new SyncPlanBuilder();
        _executor = executor ?? new SyncExecutor(
            torznab,
            sources,
            releases,
            activity,
            posterQueue,
            posterJobs,
            resolver,
            retention,
            providerStats,
            NullLogger<SyncExecutor>.Instance);
    }

    internal static CancellationTokenSource CreateManualSyncCancellationSource(
        CancellationToken requestAborted,
        CancellationToken applicationStopping)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, applicationStopping);
        cts.CancelAfter(TimeSpan.FromMinutes(5));
        return cts;
    }

    public async Task<SyncOrchestrationResult> ExecuteManualSyncAsync(Source src, bool rssOnly, CancellationToken requestAborted)
    {
        using var syncCts = CreateManualSyncCancellationSource(requestAborted, _appLifetime.ApplicationStopping);
        var syncCt = syncCts.Token;

        try
        {
            var result = await ExecuteSourceSyncAsync(src, new ManualSyncPolicy(), rssOnly, syncCt).ConfigureAwait(false);
            _providerStats.RecordSyncJob(result.Ok);

            return new SyncOrchestrationResult(
                result.Ok,
                result.UsedMode,
                result.SyncMode,
                result.ItemsCount,
                result.InsertedNew,
                result.Error);
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested || _appLifetime.ApplicationStopping.IsCancellationRequested)
        {
            _log.LogInformation(
                "Manual sync cancelled for sourceId={SourceId} requestCancelled={RequestCancelled} appStopping={AppStopping}",
                src.Id,
                requestAborted.IsCancellationRequested,
                _appLifetime.ApplicationStopping.IsCancellationRequested);
            throw;
        }
        catch (OperationCanceledException)
        {
            const string timeoutMessage = "manual sync timed out";
            _log.LogWarning("Manual sync timed out for sourceId={SourceId}", src.Id);
            _sources.UpdateLastSync(src.Id, "error", timeoutMessage);
            _activity.Add(src.Id, "error", "sync", $"Sync ERROR: {timeoutMessage}");
            _providerStats.RecordSyncJob(false);
            return new SyncOrchestrationResult(false, null, null, 0, 0, timeoutMessage);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Manual sync failed for sourceId={SourceId}", src.Id);
            var safeError = ErrorMessageSanitizer.ToOperationalMessage(ex, "sync failed");
            _sources.UpdateLastSync(src.Id, "error", safeError);
            _activity.Add(src.Id, "error", "sync", $"Sync ERROR: {safeError}");
            _providerStats.RecordSyncJob(false);
            return new SyncOrchestrationResult(false, null, null, 0, 0, safeError);
        }
    }

    public Task<SyncExecutionResult> ExecuteAutoSyncSourceAsync(Source src, CancellationToken ct)
        => ExecuteSourceSyncAsync(src, new AutoSyncPolicy(), _opts.RssOnlySync, ct);

    public Task<SyncExecutionResult> ExecuteSchedulerSourceAsync(Source src, CancellationToken ct)
        => ExecuteSourceSyncAsync(src, new SchedulerSyncPolicy(), rssOnly: false, ct);

    public async Task<bool> ExecuteSourcesAsync(IEnumerable<Source> sources, SyncPolicy policy, bool rssOnly, CancellationToken ct)
    {
        var result = await ExecuteSourcesDetailedAsync(sources, policy, rssOnly, ct).ConfigureAwait(false);
        return result.FailureCount > 0;
    }

    internal async Task<SyncSourcesRunResult> ExecuteSourcesDetailedAsync(
        IEnumerable<Source> sources,
        SyncPolicy policy,
        bool rssOnly,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var runnableSources = sources
            .Where(src => !policy.RequireEnabledSource || src.Enabled)
            .ToList();
        var totalSources = runnableSources.Count;
        var maxConcurrency = ResolveSourcesMaxConcurrency();
        var runStopwatch = Stopwatch.StartNew();
        var results = new ConcurrentBag<SourceSyncResult>();

        if (totalSources == 0)
        {
            _log.LogInformation(
                "Source sync run completed policy={Policy} sources=0 concurrency={Concurrency} elapsedMs=0 ok=0 failed=0 posterQueuePending={PosterQueuePending}",
                policy.Name,
                maxConcurrency,
                SafeGetPosterQueuePendingCount());
            return new SyncSourcesRunResult(0, 0, 0, 0, maxConcurrency, []);
        }

        using var concurrencyGate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = runnableSources.Select(src => RunSourceSyncAsync(src, policy, rssOnly, concurrencyGate, results, ct)).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            runStopwatch.Stop();
            _log.LogWarning(
                "Source sync run cancelled policy={Policy} sources={Sources} concurrency={Concurrency} elapsedMs={ElapsedMs}",
                policy.Name,
                totalSources,
                maxConcurrency,
                runStopwatch.ElapsedMilliseconds);
            throw;
        }

        runStopwatch.Stop();

        var orderedResults = results
            .OrderBy(result => result.StartedAtUtc)
            .ThenBy(result => result.SourceId)
            .ToList();
        var successCount = orderedResults.Count(result => result.Success);
        var failureCount = orderedResults.Count - successCount;

        _log.LogInformation(
            "Source sync run completed policy={Policy} sources={Sources} concurrency={Concurrency} elapsedMs={ElapsedMs} ok={OkCount} failed={FailedCount} posterQueuePending={PosterQueuePending}",
            policy.Name,
            totalSources,
            maxConcurrency,
            runStopwatch.ElapsedMilliseconds,
            successCount,
            failureCount,
            SafeGetPosterQueuePendingCount());

        return new SyncSourcesRunResult(
            totalSources,
            successCount,
            failureCount,
            runStopwatch.ElapsedMilliseconds,
            maxConcurrency,
            orderedResults);
    }

    public async Task<SyncExecutionResult> ExecuteSourceSyncAsync(Source src, SyncPolicy policy, bool rssOnly, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var input = BuildPlanInput(src, policy, rssOnly);
        var plan = _planBuilder.Build(input, policy);
        return await _executor.ExecuteAsync(plan, ct).ConfigureAwait(false);
    }

    private SyncPlanInput BuildPlanInput(Source src, SyncPolicy policy, bool rssOnly)
    {
        var settings = ResolveEffectiveSettings(policy, rssOnly);
        var categoryMap = _sources.GetCategoryMappingMap(src.Id);
        var persistedCategoryIds = _sources.GetActiveCategoryIds(src.Id)
            .Where(categoryId => categoryId > 0)
            .Distinct()
            .OrderBy(categoryId => categoryId)
            .ToList();
        var selectedCategoryIds = CategorySelection.NormalizeSelectedCategoryIds(persistedCategoryIds)
            .OrderBy(categoryId => categoryId)
            .ToList();
        var mappedCategoryIds = categoryMap.Keys
            .Where(categoryId => categoryId > 0)
            .Distinct()
            .OrderBy(categoryId => categoryId)
            .ToList();
        var unmappedCategoryIds = persistedCategoryIds
            .Where(categoryId => !categoryMap.ContainsKey(categoryId))
            .Distinct()
            .OrderBy(categoryId => categoryId)
            .ToList();
        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");

        return new SyncPlanInput(
            src,
            settings,
            categoryMap,
            persistedCategoryIds,
            selectedCategoryIds,
            mappedCategoryIds,
            unmappedCategoryIds,
            src.LastSyncAt ?? 0,
            correlationId,
            policy.Name);
    }

    private SyncEffectiveSettings ResolveEffectiveSettings(SyncPolicy policy, bool rssOnly)
    {
        var perCatLimit = _opts.RssLimitPerCategory > 0 ? _opts.RssLimitPerCategory : _opts.RssLimit;
        if (perCatLimit <= 0)
            perCatLimit = 50;

        perCatLimit = Math.Clamp(perCatLimit, policy.MinPerCategoryLimit, policy.MaxPerCategoryLimit);

        var globalLimit = _opts.RssLimitGlobalPerSource > 0 ? _opts.RssLimitGlobalPerSource : 250;
        globalLimit = Math.Clamp(globalLimit, policy.MinGlobalLimit, policy.MaxGlobalLimit);

        var defaultSeen = 0;
        try
        {
            var general = _settings.GetGeneral(new GeneralSettings
            {
                SyncIntervalMinutes = Math.Clamp(_opts.SyncIntervalMinutes, 1, 1440),
                RssLimitPerCategory = perCatLimit,
                RssLimitGlobalPerSource = globalLimit,
                RssLimit = perCatLimit,
                AutoSyncEnabled = true
            });

            perCatLimit = Math.Clamp(general.RssLimitPerCategory, policy.MinPerCategoryLimit, policy.MaxPerCategoryLimit);
            globalLimit = Math.Clamp(general.RssLimitGlobalPerSource, policy.MinGlobalLimit, policy.MaxGlobalLimit);

            var uiSettings = _settings.GetUi(new UiSettings());
            if (uiSettings.HideSeenByDefault)
                defaultSeen = 1;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read sync settings for policy={Policy}, using defaults", policy.Name);
        }

        return new SyncEffectiveSettings(
            PerCategoryLimit: perCatLimit,
            GlobalLimit: globalLimit,
            DefaultSeen: defaultSeen,
            RssOnly: rssOnly,
            EnableCategoryFallback: policy.EnableCategoryFallback,
            AllowSearchInitial: policy.AllowSearchInitial);
    }

    private async Task RunSourceSyncAsync(
        Source src,
        SyncPolicy policy,
        bool rssOnly,
        SemaphoreSlim concurrencyGate,
        ConcurrentBag<SourceSyncResult> results,
        CancellationToken ct)
    {
        await concurrencyGate.WaitAsync(ct).ConfigureAwait(false);
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await ExecuteSourceSyncAsync(src, policy, rssOnly, ct).ConfigureAwait(false);
            stopwatch.Stop();

            if (!result.Ok && IsSqliteBusy(result.Error))
            {
                _log.LogWarning(
                    "Source sync encountered SQLite contention sourceId={SourceId} name={SourceName} policy={Policy} elapsedMs={ElapsedMs} error={Error}",
                    src.Id,
                    src.Name ?? string.Empty,
                    policy.Name,
                    stopwatch.ElapsedMilliseconds,
                    result.Error ?? string.Empty);
            }

            var sourceResult = new SourceSyncResult(
                src.Id,
                src.Name ?? string.Empty,
                startedAt,
                stopwatch.ElapsedMilliseconds,
                result.Ok,
                result.Ok ? null : "SyncExecutionFailed",
                result.Error,
                SafeGetPosterQueuePendingCount());
            results.Add(sourceResult);

            if (result.Ok)
            {
                _log.LogInformation(
                    "Source sync completed sourceId={SourceId} name={SourceName} policy={Policy} elapsedMs={ElapsedMs} ok=true items={ItemsCount} insertedNew={InsertedNew} posterQueuePending={PosterQueuePending}",
                    sourceResult.SourceId,
                    sourceResult.SourceName,
                    policy.Name,
                    sourceResult.DurationMs,
                    result.ItemsCount,
                    result.InsertedNew,
                    sourceResult.PosterQueuePending);
            }
            else
            {
                _log.LogWarning(
                    "Source sync completed sourceId={SourceId} name={SourceName} policy={Policy} elapsedMs={ElapsedMs} ok=false errorType={ErrorType} error={Error} posterQueuePending={PosterQueuePending}",
                    sourceResult.SourceId,
                    sourceResult.SourceName,
                    policy.Name,
                    sourceResult.DurationMs,
                    sourceResult.ErrorType ?? "unknown",
                    sourceResult.ErrorMessage ?? string.Empty,
                    sourceResult.PosterQueuePending);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            if (IsSqliteBusy(ex))
            {
                _log.LogWarning(
                    ex,
                    "Source sync threw SQLite contention sourceId={SourceId} name={SourceName} policy={Policy} elapsedMs={ElapsedMs}",
                    src.Id,
                    src.Name ?? string.Empty,
                    policy.Name,
                    stopwatch.ElapsedMilliseconds);
            }

            var sourceResult = new SourceSyncResult(
                src.Id,
                src.Name ?? string.Empty,
                startedAt,
                stopwatch.ElapsedMilliseconds,
                false,
                ex.GetType().Name,
                ErrorMessageSanitizer.ToOperationalMessage(ex, "sync failed"),
                SafeGetPosterQueuePendingCount());
            results.Add(sourceResult);

            _log.LogError(
                ex,
                "Source sync threw sourceId={SourceId} name={SourceName} policy={Policy} elapsedMs={ElapsedMs} posterQueuePending={PosterQueuePending}",
                sourceResult.SourceId,
                sourceResult.SourceName,
                policy.Name,
                sourceResult.DurationMs,
                sourceResult.PosterQueuePending);
        }
        finally
        {
            concurrencyGate.Release();
        }
    }

    private int ResolveSourcesMaxConcurrency()
        => Math.Clamp(_opts.SyncSourcesMaxConcurrency <= 0 ? 2 : _opts.SyncSourcesMaxConcurrency, 1, 4);

    private int SafeGetPosterQueuePendingCount()
    {
        try
        {
            return _posterQueue.GetSnapshot().PendingCount;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to read poster queue snapshot");
            return -1;
        }
    }

    private static bool IsSqliteBusy(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("database is locked", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("database is busy", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("sqlite_busy", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("sql logic error", StringComparison.OrdinalIgnoreCase) && message.Contains("locked", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSqliteBusy(Exception ex)
        => IsSqliteBusy(ex.Message) || (ex.InnerException is not null && IsSqliteBusy(ex.InnerException));
}
