using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Arr;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Metadata;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Torznab;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/scheduler")]
public sealed class SchedulerController : ControllerBase
{
    private readonly SourceRepository _sources;
    private readonly ReleaseRepository _releases;
    private readonly ActivityRepository _activity;
    private readonly SettingsRepository _settings;
    private readonly RetentionService _retention;
    private readonly TorznabClient _torznab;
    private readonly IPosterFetchQueue _posterQueue;
    private readonly PosterFetchJobFactory _posterJobs;
    private readonly MediaEntityArrStatusService _entityStatus;
    private readonly ExternalIdBackfillService _externalIdBackfill;
    private readonly RequestTmdbBackfillService _requestTmdbBackfill;
    private readonly BackupExecutionCoordinator _backupCoordinator;
    private readonly AppOptions _opts;

    public SchedulerController(
        SourceRepository sources,
        ReleaseRepository releases,
        ActivityRepository activity,
        SettingsRepository settings,
        RetentionService retention,
        TorznabClient torznab,
        IPosterFetchQueue posterQueue,
        PosterFetchJobFactory posterJobs,
        MediaEntityArrStatusService entityStatus,
        ExternalIdBackfillService externalIdBackfill,
        RequestTmdbBackfillService requestTmdbBackfill,
        BackupExecutionCoordinator backupCoordinator,
        IOptions<AppOptions> opts)
    {
        _sources = sources;
        _releases = releases;
        _activity = activity;
        _settings = settings;
        _retention = retention;
        _torznab = torznab;
        _posterQueue = posterQueue;
        _posterJobs = posterJobs;
        _entityStatus = entityStatus;
        _externalIdBackfill = externalIdBackfill;
        _requestTmdbBackfill = requestTmdbBackfill;
        _backupCoordinator = backupCoordinator;
        _opts = opts.Value;
    }

    [HttpPost("run")]
    public async Task<IActionResult> Run(CancellationToken ct)
    {
        using var syncLease = _backupCoordinator.TryEnterSyncActivity("scheduler-run");
        if (syncLease is null)
            return Conflict(new { ok = false, error = "backup operation in progress" });

        var list = _sources.List().ToList();
        var perCatLimit = _opts.RssLimitPerCategory > 0 ? _opts.RssLimitPerCategory : _opts.RssLimit;
        if (perCatLimit <= 0) perCatLimit = 50;
        perCatLimit = Math.Clamp(perCatLimit, 1, 200);
        var globalLimit = _opts.RssLimitGlobalPerSource > 0 ? _opts.RssLimitGlobalPerSource : 250;
        globalLimit = Math.Clamp(globalLimit, 1, 2000);

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
            perCatLimit = Math.Clamp(general.RssLimitPerCategory, 1, 200);
            globalLimit = Math.Clamp(general.RssLimitGlobalPerSource, 1, 2000);
        }
        catch
        {
            // fallback opts
        }

        foreach (var s in list)
        {
            if (!s.Enabled) continue;

            var full = _sources.Get(s.Id);
            if (full is null) continue;

            try
            {
                var sw = Stopwatch.StartNew();
                var (items, usedMode) = await _torznab.FetchLatestAsync(
                    full.TorznabUrl, full.AuthMode, full.ApiKey ?? "", perCatLimit, ct);
                sw.Stop();
                var elapsedMs = sw.ElapsedMilliseconds;

                _sources.SaveRssMode((long)full.Id, usedMode);
                var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var name = Convert.ToString(full.Name) ?? "";
                var categoryMap = _sources.GetUnifiedCategoryMap((long)full.Id);
                var insertedNew = _releases.UpsertMany((long)full.Id, name, items, nowTs, 0, categoryMap);
                var (retentionResult, postersPurged) = _retention.ApplyRetention((long)full.Id, perCatLimit, globalLimit);

                var lastSyncAt = Convert.ToInt64(full.LastSyncAt);
                var newIds = _releases.GetNewIdsWithoutPoster((long)full.Id, lastSyncAt);
                foreach (var rid in newIds)
                {
                    var job = _posterJobs.Create(rid, forceRefresh: false);
                    if (job is null) continue;
                    _posterQueue.Enqueue(job);
                }
                _sources.UpdateLastSync((long)full.Id, "ok", null);

                _activity.Add((long)full.Id, "info", "sync", $"Manual Run OK ({items.Count} items, mode={usedMode})",
                    dataJson: $"{{\"itemsCount\":{items.Count},\"usedMode\":\"{usedMode}\",\"insertedNew\":{insertedNew},\"totalBeforeRetention\":{retentionResult.TotalBefore},\"purgedByPerCat\":{retentionResult.PurgedByPerCategory},\"purgedByGlobal\":{retentionResult.PurgedByGlobal},\"postersPurged\":{postersPurged},\"elapsedMs\":{elapsedMs}}}");
            }
            catch (Exception ex)
            {
                var safeError = ErrorMessageSanitizer.ToOperationalMessage(ex, "manual sync failed");
                _sources.UpdateLastSync((long)s.Id, "error", safeError);
                _activity.Add((long)s.Id, "error", "sync", $"Manual Run ERROR: {safeError}");
            }
        }

        return Ok(new { ok = true });
    }

    // POST /api/scheduler/media-entities/backfill?limit=200&mode=posters|external-ids|request-ids|all
    [HttpPost("media-entities/backfill")]
    public async Task<IActionResult> BackfillMediaEntities([FromQuery] int? limit, [FromQuery] string? mode, CancellationToken ct)
    {
        var lim = Math.Clamp(limit ?? 200, 1, 5000);
        var normalizedMode = (mode ?? "posters").Trim().ToLowerInvariant();

        if (normalizedMode is "posters")
        {
            var updated = _releases.BackfillEntityPosters(lim);
            return Ok(new { ok = true, mode = normalizedMode, updated });
        }

        if (normalizedMode is "external-ids" or "ids")
        {
            var result = await _externalIdBackfill.BackfillSeriesExternalIdsAsync(lim, ct);
            return Ok(new
            {
                ok = true,
                mode = "external-ids",
                missingTmdbCandidates = result.MissingTmdbCandidates,
                resolvedTmdb = result.ResolvedTmdb,
                missingTvdbCandidates = result.MissingTvdbCandidates,
                resolvedTvdb = result.ResolvedTvdb,
                propagatedToEntities = result.PropagatedToEntities
            });
        }

        if (normalizedMode is "request-ids" or "request-tmdb")
        {
            var result = await _requestTmdbBackfill.BackfillSeriesRequestTmdbAsync(lim, ct);
            return Ok(new
            {
                ok = true,
                mode = "request-ids",
                missingCandidates = result.MissingCandidates,
                reusedExistingTmdb = result.ReusedExistingTmdb,
                resolvedFromTvdbMap = result.ResolvedFromTvdbMap,
                resolvedFromTitleSearch = result.ResolvedFromTitleSearch,
                unresolved = result.Unresolved
            });
        }

        if (normalizedMode is "all")
        {
            var postersUpdated = _releases.BackfillEntityPosters(lim);
            var idsResult = await _externalIdBackfill.BackfillSeriesExternalIdsAsync(lim, ct);
            var requestIdsResult = await _requestTmdbBackfill.BackfillSeriesRequestTmdbAsync(lim, ct);
            return Ok(new
            {
                ok = true,
                mode = "all",
                postersUpdated,
                missingTmdbCandidates = idsResult.MissingTmdbCandidates,
                resolvedTmdb = idsResult.ResolvedTmdb,
                missingTvdbCandidates = idsResult.MissingTvdbCandidates,
                resolvedTvdb = idsResult.ResolvedTvdb,
                propagatedToEntities = idsResult.PropagatedToEntities,
                requestIdsMissingCandidates = requestIdsResult.MissingCandidates,
                requestIdsReusedExistingTmdb = requestIdsResult.ReusedExistingTmdb,
                requestIdsResolvedFromTvdbMap = requestIdsResult.ResolvedFromTvdbMap,
                requestIdsResolvedFromTitleSearch = requestIdsResult.ResolvedFromTitleSearch,
                requestIdsUnresolved = requestIdsResult.Unresolved
            });
        }

        return BadRequest(new { ok = false, error = "mode must be posters, external-ids, ids, request-ids, request-tmdb, or all" });
    }

    // POST /api/scheduler/media-entities/arr-status/refresh?limit=500
    [HttpPost("media-entities/arr-status/refresh")]
    public IActionResult RefreshEntityArrStatus([FromQuery] int? limit)
    {
        var updated = _entityStatus.RefreshStaleStatuses(limit ?? 500);
        return Ok(new { ok = true, updated });
    }
}
