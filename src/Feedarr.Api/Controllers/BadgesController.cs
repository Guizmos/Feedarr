using Microsoft.AspNetCore.Mvc;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Backup;
using Microsoft.Extensions.Caching.Memory;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/badges")]
public sealed class BadgesController : ControllerBase
{
    private readonly BadgeSignal _signal;
    private readonly Db _db;
    private readonly ActivityRepository _activity;
    private readonly SettingsRepository _settings;
    private readonly BackupExecutionCoordinator _backupCoordinator;
    private readonly IMemoryCache _cache;
    private readonly ILogger<BadgesController> _log;

    public BadgesController(
        BadgeSignal signal,
        Db db,
        ActivityRepository activity,
        SettingsRepository settings,
        BackupExecutionCoordinator backupCoordinator,
        IMemoryCache cache,
        ILogger<BadgesController> log)
    {
        _signal = signal;
        _db = db;
        _activity = activity;
        _settings = settings;
        _backupCoordinator = backupCoordinator;
        _cache = cache;
        _log = log;
    }

    private static readonly TimeSpan SseTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SummaryCacheDuration = TimeSpan.FromSeconds(3);

    [HttpGet("summary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Summary(
        [FromQuery] long? activitySinceTs = null,
        [FromQuery] long? releasesSinceTs = null,
        [FromQuery] int? activityLimit = null)
    {
        var safeActivitySinceTs = Math.Max(0L, activitySinceTs ?? 0L);
        var safeReleasesSinceTs = Math.Max(0L, releasesSinceTs ?? 0L);
        var safeActivityLimit = Math.Clamp(activityLimit ?? 200, 1, 500);
        var cacheKey = $"badges:summary:v1:{safeActivitySinceTs}:{safeReleasesSinceTs}:{safeActivityLimit}";

        if (_cache.TryGetValue<object>(cacheKey, out var cached) && cached is not null)
        {
            _log.LogDebug(
                "BadgesSummary cache hit activitySinceTs={ActivitySinceTs} releasesSinceTs={ReleasesSinceTs} limit={Limit}",
                safeActivitySinceTs,
                safeReleasesSinceTs,
                safeActivityLimit);
            return Ok(cached);
        }

        var ui = _settings.GetUi(UiSettings.BuildDefaults());
        var activity = _activity.GetBadgeSummary(
            safeActivitySinceTs,
            safeActivityLimit,
            includeInfo: ui.BadgeInfo,
            includeWarn: ui.BadgeWarn,
            includeError: ui.BadgeError);

        int sourcesCount;
        int releasesCount;
        long releasesLatestTs;
        int? releasesNewSinceTsCount = null;

        using (var conn = _db.Open())
        {
            using var stats = conn.QueryMultiple(
                """
                SELECT COUNT(1) FROM sources;
                SELECT COUNT(1) FROM releases;
                SELECT COALESCE(MAX(created_at_ts), 0) FROM releases;
                """);

            sourcesCount = stats.ReadSingle<int>();
            releasesCount = stats.ReadSingle<int>();
            releasesLatestTs = stats.ReadSingle<long>();

            if (safeReleasesSinceTs > 0)
            {
                releasesNewSinceTsCount = conn.ExecuteScalar<int>(
                    """
                    SELECT COUNT(1)
                    FROM releases
                    WHERE created_at_ts > @sinceTs;
                    """,
                    new { sinceTs = safeReleasesSinceTs });
            }
        }

        var ext = _settings.GetExternalFlags();
        var missingExternalCount = 0;
        if (!ext.hasTmdbApiKey) missingExternalCount++;
        if (!ext.hasIgdbClientId) missingExternalCount++;
        if (!ext.hasIgdbClientSecret) missingExternalCount++;

        var maintenance = _settings.GetMaintenance(new MaintenanceSettings());
        var backupState = _backupCoordinator.GetState();

        var payload = new
        {
            activity = new
            {
                unreadCount = activity.UnreadCount,
                lastActivityTs = activity.LastActivityTs,
                tone = activity.Tone
            },
            releases = new
            {
                totalCount = releasesCount,
                latestTs = releasesLatestTs,
                newSinceTsCount = releasesNewSinceTsCount
            },
            system = new
            {
                isSyncRunning = backupState.ActiveSyncActivities > 0,
                schedulerBusy = backupState.IsBusy || backupState.SyncBlocked,
                updatesBadge = false,
                sourcesCount
            },
            settings = new
            {
                missingExternalCount,
                hasAdvancedMaintenanceEnabled = maintenance.MaintenanceAdvancedOptionsEnabled
            }
        };

        _cache.Set(cacheKey, payload, SummaryCacheDuration);
        _log.LogDebug(
            "BadgesSummary cache miss activitySinceTs={ActivitySinceTs} releasesSinceTs={ReleasesSinceTs} limit={Limit}",
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
