using Feedarr.Api.Services.Matching.Replay;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/diagnostics/matching-shadow/replay")]
public sealed class MatchingReplayController(MatchingReplayService replay) : ControllerBase
{
    private bool Allowed()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        return HttpContext.Items[BasicAuthMiddleware.AuthPassedKey] is true && HttpContext.Items[BasicAuthMiddleware.BootstrapTokenActiveKey] is not true;
    }
    [HttpGet]
    public IActionResult Get() => Allowed() ? Ok(replay.Snapshot()) : Unauthorized();
    [HttpPost]
    public IActionResult Start(ReplayRequest request)
    {
        if (!Allowed()) return Unauthorized();
        try { return Accepted(replay.Start(request)); }
        catch (ArgumentException) { return BadRequest(new { error = "replay_request_invalid" }); }
        catch (InvalidOperationException) { return Conflict(new { error = "replay_busy_or_state_changed" }); }
    }
    [HttpPost("stop")]
    public IActionResult Stop() => Allowed() ? Ok(replay.Stop()) : Unauthorized();
    [HttpPost("reset")]
    public IActionResult Reset() => Allowed() ? Ok(replay.Stop(reset: true)) : Unauthorized();
    [HttpGet("export")]
    public IActionResult Export()
    {
        if (!Allowed()) return Unauthorized();
        // Same allowlisted/sanitized DTO as the UI; no raw responses, URLs, secrets or indexer objects.
        Response.Headers.ContentDisposition = "attachment; filename=feedarr-matching-replay.json";
        return Ok(replay.Snapshot());
    }
}
