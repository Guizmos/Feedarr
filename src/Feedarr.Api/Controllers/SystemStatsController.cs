using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/system")]
public sealed class SystemStatsController : ControllerBase
{
    private readonly SystemApiCore _core;

    public SystemStatsController(SystemApiCore core)
    {
        _core = core;
    }

    [EnableRateLimiting("stats-heavy")]
    [HttpGet("stats/summary")]
    public Task<IActionResult> StatsSummary(CancellationToken ct)
        => _core.StatsSummary(ct);

    [EnableRateLimiting("stats-heavy")]
    [HttpGet("stats/feedarr")]
    public Task<IActionResult> StatsFeedarr([FromQuery] int days = 30, CancellationToken ct = default)
        => _core.StatsFeedarr(days, ct);

    [EnableRateLimiting("stats-heavy")]
    [HttpGet("stats/indexers")]
    public IActionResult StatsIndexers(
        [FromQuery] int limit = 100,
        [FromQuery] string? cursor = null,
        [FromQuery] string direction = "next",
        [FromQuery] int offset = 0)
        => _core.StatsIndexers(limit, cursor, direction, offset);

    [EnableRateLimiting("stats-heavy")]
    [HttpGet("stats/providers")]
    public IActionResult StatsProviders(
        [FromQuery] int limit = 100,
        [FromQuery] string? cursor = null,
        [FromQuery] string direction = "next",
        [FromQuery] int offset = 0)
        => _core.StatsProviders(limit, cursor, direction, offset);

    [EnableRateLimiting("stats-heavy")]
    [HttpGet("stats/releases")]
    public IActionResult StatsReleases()
        => _core.StatsReleases();

    [EnableRateLimiting("stats-heavy")]
    [HttpGet("stats")]
    public Task<IActionResult> Stats(CancellationToken ct)
        => _core.Stats(ct);
}
