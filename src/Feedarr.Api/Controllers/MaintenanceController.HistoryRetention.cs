using System.Text.Json;
using Feedarr.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Feedarr.Api.Controllers;

public sealed partial class MaintenanceController
{
    [HttpGet("history-cleanup")]
    public Task<IActionResult> PreviewHistoryCleanup([FromServices] RetentionService retention,
        CancellationToken ct, [FromQuery] int limit = 100, [FromQuery] int scanLimit = 1000,
        [FromQuery] string? afterPoster = null, [FromQuery] string? afterStore = null)
        => HistoryCleanup(retention, new(true, limit, scanLimit, afterPoster, afterStore), ct);

    [HttpPost("history-cleanup")]
    public async Task<IActionResult> HistoryCleanup([FromServices] RetentionService retention,
        [FromBody] HistoryCleanupRequest? request, CancellationToken ct)
    {
        request ??= new(); // simulation is the default, including an empty body
        if (!_maintenanceLock.TryEnter()) return Conflict(new { error = "Une maintenance est déjà en cours." });
        try
        {
            var result = await retention.CleanupHistoryAsync(request, ct);
            if (!request.DryRun)
                _activity.Add(null, result.Errors > 0 ? "warn" : "info", "history_cleanup",
                    "Bounded history cleanup completed", JsonSerializer.Serialize(result));
            return Ok(result);
        }
        catch (ArgumentException) { return BadRequest(new { error = "Paramètres invalides : limite 1–100, analyse 1–5000 et confirmation explicite requise pour supprimer." }); }
        catch (InvalidOperationException) { return Conflict(new { error = "Nettoyage indisponible : vérifiez les opérations de maintenance ou de restauration en cours avant de réessayer." }); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "History cleanup failed");
            return StatusCode(500, new { error = "Le nettoyage a échoué. Consultez les journaux avant de recommencer." });
        }
        finally { _maintenanceLock.Release(); }
    }
}
