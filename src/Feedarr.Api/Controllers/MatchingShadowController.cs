using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/diagnostics/matching-shadow")]
public sealed class MatchingShadowController(PosterFetchService posters) : ControllerBase
{
    public sealed record SwitchRequest(bool Enabled, long? Generation = null);

    private bool Authenticated => HttpContext.Items[BasicAuthMiddleware.AuthPassedKey] is true;
    private void PrivateResponse()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }

    [HttpGet]
    public IActionResult Get()
    {
        PrivateResponse();
        return Authenticated ? Ok(posters.ShadowDiagnostics.Snapshot()) : Unauthorized();
    }
    [HttpPut]
    public IActionResult Set(SwitchRequest request)
    {
        PrivateResponse();
        if (!Authenticated) return Unauthorized();
        if (request.Enabled && !request.Generation.HasValue) return BadRequest(new { error = "shadow_generation_required" });
        return Ok(posters.ShadowDiagnostics.SetEnabled(request.Enabled, request.Generation));
    }
    [HttpPost("reset")]
    public IActionResult Reset()
    {
        PrivateResponse();
        return Authenticated ? Ok(posters.ShadowDiagnostics.Reset()) : Unauthorized();
    }
}
