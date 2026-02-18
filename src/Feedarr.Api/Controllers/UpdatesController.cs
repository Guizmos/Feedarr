using Feedarr.Api.Services.Updates;
using Microsoft.AspNetCore.Mvc;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/updates")]
public sealed class UpdatesController : ControllerBase
{
    private readonly ReleaseInfoService _releaseInfoService;

    public UpdatesController(ReleaseInfoService releaseInfoService)
    {
        _releaseInfoService = releaseInfoService;
    }

    [HttpGet("latest")]
    public async Task<IActionResult> Latest([FromQuery] bool force = false, CancellationToken ct = default)
    {
        var result = await _releaseInfoService.GetLatestAsync(force, ct);
        return Ok(UpdateDtoMapper.ToDto(result));
    }
}
