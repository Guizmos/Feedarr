using Microsoft.AspNetCore.Mvc;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Arr;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Metadata;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Sync;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/scheduler")]
public sealed class SchedulerController : ControllerBase
{
    private readonly SourceRepository _sources;
    private readonly ReleaseRepository _releases;
    private readonly ActivityRepository _activity;
    private readonly SyncOrchestrationService _syncOrchestration;
    private readonly MediaEntityArrStatusService _entityStatus;
    private readonly ExternalIdBackfillService _externalIdBackfill;
    private readonly RequestTmdbBackfillService _requestTmdbBackfill;
    private readonly TmdbMetadataBackfillService _tmdbMetadataBackfill;
    private readonly BackupExecutionCoordinator _backupCoordinator;
    private readonly ILogger<SchedulerController> _log;

    public SchedulerController(
        SourceRepository sources,
        ReleaseRepository releases,
        ActivityRepository activity,
        SyncOrchestrationService syncOrchestration,
        MediaEntityArrStatusService entityStatus,
        ExternalIdBackfillService externalIdBackfill,
        RequestTmdbBackfillService requestTmdbBackfill,
        TmdbMetadataBackfillService tmdbMetadataBackfill,
        BackupExecutionCoordinator backupCoordinator,
        ILogger<SchedulerController> log)
    {
        _sources = sources;
        _releases = releases;
        _activity = activity;
        _syncOrchestration = syncOrchestration;
        _entityStatus = entityStatus;
        _externalIdBackfill = externalIdBackfill;
        _requestTmdbBackfill = requestTmdbBackfill;
        _tmdbMetadataBackfill = tmdbMetadataBackfill;
        _backupCoordinator = backupCoordinator;
        _log = log;
    }

    [HttpPost("run")]
    public async Task<IActionResult> Run(CancellationToken ct)
    {
        using var syncLease = _backupCoordinator.TryEnterSyncActivity("scheduler-run");
        if (syncLease is null)
            return Conflict(new { ok = false, error = "backup operation in progress" });

        var sources = _sources.List()
            .Where(source => source.Enabled)
            .Select(source => _sources.Get(source.Id))
            .Where(source => source is not null)
            .Cast<Feedarr.Api.Models.Source>()
            .ToList();

        _log.LogInformation("Scheduler sync run starting sources={Count}", sources.Count);
        await _syncOrchestration.ExecuteSourcesAsync(sources, new SchedulerSyncPolicy(), rssOnly: false, ct).ConfigureAwait(false);

        return Ok(new { ok = true });
    }

    // POST /api/scheduler/media-entities/backfill?limit=200&mode=posters|external-ids|request-ids|metadata|all
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

        if (normalizedMode is "metadata" or "tmdb-metadata")
        {
            var result = await _tmdbMetadataBackfill.BackfillMissingTmdbMetadataAsync(lim, ct);
            return Ok(new
            {
                ok = true,
                mode = "metadata",
                scanned = result.Scanned,
                eligible = result.Eligible,
                processed = result.Processed,
                localPropagated = result.LocalPropagated,
                tmdbRefreshed = result.TmdbRefreshed,
                uniqueTmdbKeysRefreshed = result.UniqueTmdbKeysRefreshed,
                skipped = result.Skipped,
                errors = result.Errors
            });
        }

        if (normalizedMode is "all")
        {
            var postersUpdated = _releases.BackfillEntityPosters(lim);
            var idsResult = await _externalIdBackfill.BackfillSeriesExternalIdsAsync(lim, ct);
            var requestIdsResult = await _requestTmdbBackfill.BackfillSeriesRequestTmdbAsync(lim, ct);
            var metadataResult = await _tmdbMetadataBackfill.BackfillMissingTmdbMetadataAsync(lim, ct);
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
                requestIdsUnresolved = requestIdsResult.Unresolved,
                metadataScanned = metadataResult.Scanned,
                metadataEligible = metadataResult.Eligible,
                metadataProcessed = metadataResult.Processed,
                metadataLocalPropagated = metadataResult.LocalPropagated,
                metadataTmdbRefreshed = metadataResult.TmdbRefreshed,
                metadataUniqueTmdbKeysRefreshed = metadataResult.UniqueTmdbKeysRefreshed,
                metadataSkipped = metadataResult.Skipped,
                metadataErrors = metadataResult.Errors
            });
        }

        return BadRequest(new { ok = false, error = "mode must be posters, external-ids, ids, request-ids, request-tmdb, metadata, tmdb-metadata, or all" });
    }

    // POST /api/scheduler/media-entities/arr-status/refresh?limit=500
    [HttpPost("media-entities/arr-status/refresh")]
    public IActionResult RefreshEntityArrStatus([FromQuery] int? limit)
    {
        var updated = _entityStatus.RefreshStaleStatuses(limit ?? 500);
        return Ok(new { ok = true, updated });
    }
}
