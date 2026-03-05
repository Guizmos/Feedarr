using Microsoft.AspNetCore.Mvc;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/badges")]
public sealed class BadgesController : ControllerBase
{
    private readonly BadgeSignal _signal;
    private readonly Db _db;
    private readonly ActivityRepository _activity;
    private readonly BadgesSummaryCacheService _summaryCache;
    private readonly ILogger<BadgesController> _log;

    public BadgesController(
        BadgeSignal signal,
        Db db,
        ActivityRepository activity,
        BadgesSummaryCacheService summaryCache,
        ILogger<BadgesController> log)
    {
        _signal = signal;
        _db = db;
        _activity = activity;
        _summaryCache = summaryCache;
        _log = log;
    }

    private static readonly TimeSpan SseTimeout = TimeSpan.FromMinutes(30);

    [HttpGet("summary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Summary(
        [FromQuery] long? activitySinceTs = null,
        [FromQuery] long? releasesSinceTs = null,
        [FromQuery] int? activityLimit = null)
    {
        var safeActivitySinceTs = Math.Max(0L, activitySinceTs ?? 0L);
        var safeReleasesSinceTs = Math.Max(0L, releasesSinceTs ?? 0L);
        var safeActivityLimit = Math.Clamp(activityLimit ?? 200, 1, 500);

        var requestAborted = HttpContext?.RequestAborted ?? CancellationToken.None;
        var baseSummary = await _summaryCache.GetBaseSummaryAsync(requestAborted).ConfigureAwait(false);
        var activity = _activity.GetBadgeDeltaSummary(
            safeActivitySinceTs,
            safeActivityLimit,
            includeInfo: baseSummary.IncludeInfo,
            includeWarn: baseSummary.IncludeWarn,
            includeError: baseSummary.IncludeError);

        int? releasesNewSinceTsCount = null;
        if (safeReleasesSinceTs > 0)
        {
            using var conn = _db.Open();
            releasesNewSinceTsCount = conn.ExecuteScalar<int>(
                """
                SELECT COUNT(1)
                FROM releases
                WHERE created_at_ts > @sinceTs;
                """,
                new { sinceTs = safeReleasesSinceTs });
        }

        var payload = new
        {
            activity = new
            {
                unreadCount = activity.UnreadCount,
                lastActivityTs = baseSummary.LastActivityTs,
                tone = activity.Tone
            },
            releases = new
            {
                totalCount = baseSummary.ReleasesCount,
                latestTs = baseSummary.ReleasesLatestTs,
                newSinceTsCount = releasesNewSinceTsCount
            },
            system = new
            {
                isSyncRunning = baseSummary.IsSyncRunning,
                schedulerBusy = baseSummary.SchedulerBusy,
                updatesBadge = baseSummary.UpdatesBadge,
                sourcesCount = baseSummary.SourcesCount
            },
            settings = new
            {
                missingExternalCount = baseSummary.MissingExternalCount,
                hasAdvancedMaintenanceEnabled = baseSummary.HasAdvancedMaintenanceEnabled
            }
        };

        _log.LogDebug(
            "BadgesSummary computed using shared base activitySinceTs={ActivitySinceTs} releasesSinceTs={ReleasesSinceTs} limit={Limit}",
            safeActivitySinceTs,
            safeReleasesSinceTs,
            safeActivityLimit);

        return Ok(payload);
    }

    [HttpGet("stream")]
    public async Task Stream(CancellationToken ct)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SseTimeout);
        var linked = cts.Token;

        await Response.WriteAsync("event: ready\ndata: ok\n\n", linked);
        await Response.Body.FlushAsync(linked);

        try
        {
            await foreach (var type in _signal.Subscribe(linked))
            {
                // Legacy event name kept for old clients.
                await Response.WriteAsync($"event: badge\ndata: {type}\n\n", linked);
                // New stable event name used by modern clients.
                await Response.WriteAsync($"event: badges-changed\ndata: {type}\n\n", linked);
                await Response.Body.FlushAsync(linked);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // SSE timeout reached — client will reconnect
        }
    }
}
