using Microsoft.AspNetCore.Mvc;
using Feedarr.Api.Services.Jackett;
using Feedarr.Api.Data.Repositories;

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
        if (string.IsNullOrWhiteSpace(apiKey))
            return BadRequest(new { error = "apiKey missing" });

        try
        {
            var list = await _jackett.ListIndexersAsync(baseUrl, apiKey, ct);
            var result = list.Select(x => new
            {
                id = x.id,
                name = x.name,
                torznabUrl = x.torznabUrl
            }).ToList();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Jackett indexers listing failed for sourceId={SourceId}", dto?.SourceId);
            return StatusCode(500, new { error = "internal server error" });
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
