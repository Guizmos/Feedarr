using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Backup.Configuration;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/configuration-backups")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[EnableRateLimiting("configuration-backup")]
[RequestSizeLimit(ConfigurationArchiveService.MaxArchiveBytes + 16384)]
[RequestFormLimits(MultipartBodyLengthLimit = ConfigurationArchiveService.MaxArchiveBytes + 16384, ValueLengthLimit = 4096)]
public sealed class ConfigurationBackupsController(ConfigurationArchiveService archives, ConfigurationRestoreService restore) : ControllerBase
{
    public sealed record ExportRequest(string Password, string Confirmation);
    [HttpPost("export")]
    public Task<IActionResult> Export(ExportRequest request, CancellationToken ct) => Execute(async () =>
    {
        if (request.Password != request.Confirmation) return BadRequest(new { message = "Les mots de passe ne correspondent pas." });
        var bytes = await archives.ExportAsync(request.Password, ct);
        return File(bytes, "application/octet-stream", "feedarr-configuration.feedarr");
    });
    [HttpPost("preview")]
    public Task<IActionResult> Preview(IFormFile file, [FromForm] string password, CancellationToken ct) => Execute(async () =>
    {
        if (file.Length is <= 0 or > ConfigurationArchiveService.MaxArchiveBytes) return BadRequest(new { message = "Fichier vide ou trop volumineux." });
        using var input = file.OpenReadStream(); return Ok(await restore.PreviewAsync(input, password, ct));
    });
    [HttpPost("restore")]
    public Task<IActionResult> Restore(IFormFile file, [FromForm] string password, [FromForm] string expectedState, [FromForm] bool confirm, CancellationToken ct) => Execute(async () =>
    {
        if (!confirm) return BadRequest(new { message = "Confirmez la restauration." });
        if (file.Length is <= 0 or > ConfigurationArchiveService.MaxArchiveBytes) return BadRequest(new { message = "Fichier vide ou trop volumineux." });
        using var input = file.OpenReadStream(); return Ok(await restore.ApplyAsync(input, password, expectedState, ct));
    });
    private async Task<IActionResult> Execute(Func<Task<IActionResult>> action)
    {
        if (HttpContext.Items[BasicAuthMiddleware.AuthPassedKey] is not true) return Unauthorized();
        try { return await action(); }
        catch (ConfigurationArchiveException ex) { return StatusCode(422, new { code = ex.Code.ToString(), message = "Sauvegarde invalide, mot de passe incorrect ou fichier endommagé." }); }
        catch (BackupOperationException ex) { return StatusCode(ex.StatusCode, new { message = ErrorMessageSanitizer.ToOperationalMessage(ex, "La restauration n’a pas été appliquée.") }); }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return StatusCode(499); }
        catch (Exception) { return StatusCode(500, new { message = "L’opération a échoué. Aucune configuration partielle n’est conservée." }); }
    }
}
