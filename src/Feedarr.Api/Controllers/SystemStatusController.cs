using Microsoft.AspNetCore.Mvc;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/system")]
public sealed class SystemStatusController : ControllerBase
{
    private readonly SystemApiCore _core;

    public SystemStatusController(SystemApiCore core)
    {
        _core = core;
    }

    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<IActionResult> Status([FromQuery] long? releasesSinceTs = null, CancellationToken ct = default)
        => _core.Status(releasesSinceTs, ct);

    [HttpGet("providers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Providers()
        => _core.Providers();

    [HttpGet("perf")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Performance([FromQuery] int top = 20)
        => _core.Performance(top);

    [HttpGet("onboarding")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Onboarding()
        => _core.Onboarding();

    [HttpPost("onboarding/complete")]
    public IActionResult CompleteOnboarding()
        => _core.CompleteOnboarding();

    [HttpPost("onboarding/reset")]
    public IActionResult ResetOnboarding()
        => _core.ResetOnboarding();

    [HttpGet("storage")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<IActionResult> Storage(CancellationToken ct)
        => _core.Storage(ct);
}
