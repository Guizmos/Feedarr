using Microsoft.AspNetCore.Mvc;
using Feedarr.Api.Services.Jackett;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/jackett")]
public sealed class JackettController : ControllerBase
{
    private readonly JackettClient _jackett;
    private readonly SourceRepository _sources;
    private readonly ILogger<JackettController> _log;

    public JackettController(
        JackettClient jackett,
        SourceRepository sources,
        ILogger<JackettController> log)
    {
        _jackett = jackett;
        _sources = sources;
        _log = log;
    }

    public sealed class JackettIndexersRequest
    {
        public string? BaseUrl { get; set; }
        public string? ApiKey { get; set; }
        public long? SourceId { get; set; }
    }

    [HttpPost("indexers")]
    public async Task<IActionResult> ListIndexers([FromBody] JackettIndexersRequest dto, CancellationToken ct)
    {
        var baseUrl = (dto?.BaseUrl ?? "").Trim();
        var apiKey = (dto?.ApiKey ?? "").Trim();

        if (dto?.SourceId is > 0)
        {
            var src = _sources.Get(dto.SourceId.Value);
            if (src is null)
                return NotFound(new { error = "source not found" });

            baseUrl = ExtractBaseUrl(src.TorznabUrl);
            if (!string.IsNullOrWhiteSpace(src.ApiKey))
                apiKey = src.ApiKey;
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
            return BadRequest(new { error = "baseUrl missing" });
        if (!OutboundUrlGuard.TryNormalizeProviderBaseUrl("jackett", baseUrl, out var normalizedBaseUrl, out var baseUrlError))
            return BadRequest(new { error = baseUrlError });
        if (string.IsNullOrWhiteSpace(apiKey))
            return BadRequest(new { error = "apiKey missing" });

        try
        {
            var list = await _jackett.ListIndexersAsync(normalizedBaseUrl, apiKey, ct);
            var result = list.Select(x => new
            {
                id = x.id,
                name = x.name,
                torznabUrl = x.torznabUrl
            }).ToList();
            return Ok(result);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "Jackett upstream error for sourceId={SourceId}", dto?.SourceId);
            return StatusCode(502, new { error = ex.Message ?? "upstream provider unavailable" });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("redirect", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning(ex, "Jackett redirect loop for sourceId={SourceId}", dto?.SourceId);
            return StatusCode(502, new { error = "Trop de redirections — vérifiez l'URL et la clé API" });
        }
        catch (TaskCanceledException)
        {
            return StatusCode(504, new { error = "upstream provider timeout" });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Jackett indexers listing failed for sourceId={SourceId}", dto?.SourceId);
            return StatusCode(500, new { error = ex.Message ?? "internal server error" });
        }
    }

    private static string ExtractBaseUrl(string torznabUrl)
    {
        if (string.IsNullOrWhiteSpace(torznabUrl)) return "";
        var marker = "/api/v2.0/indexers/";
        var idx = torznabUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx > 0) return torznabUrl[..idx];
        return torznabUrl.TrimEnd('/');
    }
}
