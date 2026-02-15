using Microsoft.AspNetCore.Mvc;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services.Posters;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/activity")]
public sealed class ActivityController : ControllerBase
{
    private readonly ActivityRepository _repo;
    private readonly RetroFetchLogService _retroLogs;

    public ActivityController(ActivityRepository repo, RetroFetchLogService retroLogs)
    {
        _repo = repo;
        _retroLogs = retroLogs;
    }

    // GET /api/activity?limit=50&sourceId=1&eventType=sync
    [HttpGet]
    public IActionResult List(
        [FromQuery] int? limit,
        [FromQuery] long? sourceId,
        [FromQuery] string? eventType,
        [FromQuery] string? level)
    {
        var safeLimit = Math.Clamp(limit ?? 100, 1, 500);
        return Ok(_repo.List(safeLimit, sourceId, eventType, level));
    }

    // POST /api/activity/purge
    [HttpPost("purge")]
    public IActionResult Purge([FromQuery] string? scope)
    {
        var normalized = string.IsNullOrWhiteSpace(scope) ? "all" : scope.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "all":
                _repo.PurgeAll();
                break;
            case "history":
                _repo.PurgeHistory();
                break;
            case "logs":
                _repo.PurgeLogs();
                break;
            default:
                return BadRequest(new { error = "invalid purge scope" });
        }

        // Also delete retro-fetch CSV log files
        var csvDeleted = 0;
        if (normalized is "all" or "logs")
            csvDeleted = _retroLogs.PurgeLogFiles();

        return Ok(new { ok = true, scope = normalized, csvDeleted });
    }
}
