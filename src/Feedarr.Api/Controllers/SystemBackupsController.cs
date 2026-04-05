using Microsoft.AspNetCore.Mvc;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/system")]
public sealed class SystemBackupsController : ControllerBase
{
    private readonly SystemApiCore _core;

    public SystemBackupsController(SystemApiCore core)
    {
        _core = core;
    }

    [HttpGet("backups")]
    public IActionResult Backups()
        => _core.Backups();

    [HttpGet("backups/state")]
    public IActionResult BackupState()
        => _core.BackupState();

    [HttpPost("backups/purge")]
    public Task<IActionResult> PurgeBackups(CancellationToken ct)
        => _core.PurgeBackups(ct);

    [HttpPost("backups")]
    public Task<IActionResult> CreateBackup(CancellationToken ct)
        => _core.CreateBackup(ct);

    [HttpDelete("backups/{name}")]
    public Task<IActionResult> DeleteBackup([FromRoute] string name, CancellationToken ct)
        => _core.DeleteBackup(name, ct);

    [HttpGet("backups/{name}/download")]
    public IActionResult DownloadBackup([FromRoute] string name)
        => _core.DownloadBackup(name);

    [HttpPost("backups/{name}/restore")]
    public Task<IActionResult> RestoreBackup([FromRoute] string name, [FromQuery] bool confirm = false, CancellationToken ct = default)
        => _core.RestoreBackup(name, confirm, ct);
}
