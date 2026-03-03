using System.Diagnostics;
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
        var hadFailure = false;
        foreach (var src in sources)
        {
            ct.ThrowIfCancellationRequested();
            if (policy.RequireEnabledSource && !src.Enabled)
                continue;

            var result = await ExecuteSourceSyncAsync(src, policy, rssOnly, ct).ConfigureAwait(false);
            if (!result.Ok)
                hadFailure = true;
        }

        return hadFailure;
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
}
