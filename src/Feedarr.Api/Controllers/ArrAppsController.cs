using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Dtos.Arr;
using Feedarr.Api.Services.Arr;
using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/apps")]
public sealed class ArrAppsController : ControllerBase
{
    private readonly ArrApplicationRepository _repo;
    private readonly SonarrClient _sonarr;
    private readonly RadarrClient _radarr;
    private readonly ArrLibrarySyncService _syncService;
    private readonly ActivityRepository _activity;
    private readonly ILogger<ArrAppsController> _log;

    public ArrAppsController(
        ArrApplicationRepository repo,
        SonarrClient sonarr,
        RadarrClient radarr,
        ArrLibrarySyncService syncService,
        ActivityRepository activity,
        ILogger<ArrAppsController> log)
    {
        _repo = repo;
        _sonarr = sonarr;
        _radarr = radarr;
        _syncService = syncService;
        _activity = activity;
        _log = log;
    }

    private static List<int> ParseTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<int>>(tagsJson) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static string SerializeTags(List<int>? tags)
    {
        if (tags is null || tags.Count == 0) return "[]";
        return JsonSerializer.Serialize(tags);
    }

    private ArrAppResponseDto ToResponseDto(Models.Arr.ArrApplication app)
    {
        return new ArrAppResponseDto
        {
            Id = app.Id,
            Type = app.Type,
            Name = app.Name,
            BaseUrl = app.BaseUrl,
            HasApiKey = !string.IsNullOrWhiteSpace(app.ApiKeyEncrypted),
            IsEnabled = app.IsEnabled,
            IsDefault = app.IsDefault,
            RootFolderPath = app.RootFolderPath,
            QualityProfileId = app.QualityProfileId,
            Tags = ParseTags(app.Tags),
            SeriesType = app.SeriesType,
            SeasonFolder = app.SeasonFolder,
            MonitorMode = app.MonitorMode,
            SearchMissing = app.SearchMissing,
            SearchCutoff = app.SearchCutoff,
            MinimumAvailability = app.MinimumAvailability,
            SearchForMovie = app.SearchForMovie
        };
    }

    // GET /api/apps
    [HttpGet]
    public IActionResult List()
    {
        var apps = _repo.List();
        var response = apps.Select(ToResponseDto).ToList();
        return Ok(response);
    }

    // GET /api/apps/{id}
    [HttpGet("{id:long}")]
    public IActionResult Get([FromRoute] long id)
    {
        var app = _repo.Get(id);
        if (app is null) return NotFound(new { error = "app not found" });
        return Ok(ToResponseDto(app));
    }

    // POST /api/apps
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ArrAppCreateDto dto, CancellationToken ct)
    {
        var type = (dto.Type ?? "").Trim().ToLowerInvariant();
        if (type != "sonarr" && type != "radarr")
            return BadRequest(new { error = "type must be 'sonarr' or 'radarr'" });

        if (!OutboundUrlGuard.TryNormalizeArrBaseUrl(dto.BaseUrl, out var baseUrl, out var baseUrlError))
            return BadRequest(new { error = baseUrlError });

        var apiKey = (dto.ApiKey ?? "").Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
            return BadRequest(new { error = "apiKey missing" });

        var id = _repo.Create(
            type,
            dto.Name?.Trim(),
            baseUrl,
            apiKey,
            dto.RootFolderPath?.Trim(),
            dto.QualityProfileId,
            SerializeTags(dto.Tags),
            dto.SeriesType?.Trim(),
            dto.SeasonFolder ?? true,
            dto.MonitorMode?.Trim(),
            dto.SearchMissing ?? true,
            dto.SearchCutoff ?? false,
            dto.MinimumAvailability?.Trim(),
            dto.SearchForMovie ?? true
        );

        _activity.Add(null, "info", "arr", $"Arr app added: {dto.Name ?? type} ({type})",
            dataJson: $"{{\"appId\":{id},\"type\":\"{type}\"}}");

        // Trigger library sync for the new app
        try
        {
            await _syncService.SyncAppAsync(id, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Initial Arr sync failed after app creation for appId={AppId}", id);
            _activity.Add(null, "warn", "arr", "Arr app created but initial sync failed",
                dataJson: $"{{\"appId\":{id},\"type\":\"{type}\"}}");
        }

        var created = _repo.Get(id);
        return Ok(created is not null ? ToResponseDto(created) : new { id });
    }

    // PUT /api/apps/{id}
    [HttpPut("{id:long}")]
    public IActionResult Update([FromRoute] long id, [FromBody] ArrAppUpdateDto dto)
    {
        var current = _repo.Get(id);
        if (current is null) return NotFound(new { error = "app not found" });

        string? apiKeyMaybe = null;
        if (!string.IsNullOrWhiteSpace(dto.ApiKey))
            apiKeyMaybe = dto.ApiKey.Trim();

        string? baseUrlMaybe = null;
        if (!string.IsNullOrWhiteSpace(dto.BaseUrl))
        {
            if (!OutboundUrlGuard.TryNormalizeArrBaseUrl(dto.BaseUrl, out var normalizedBaseUrl, out var baseUrlError))
                return BadRequest(new { error = baseUrlError });
            baseUrlMaybe = normalizedBaseUrl;
        }

        var ok = _repo.Update(
            id,
            dto.Name?.Trim(),
            baseUrlMaybe,
            apiKeyMaybe,
            dto.RootFolderPath?.Trim(),
            dto.QualityProfileId,
            dto.Tags is not null ? SerializeTags(dto.Tags) : null,
            dto.SeriesType?.Trim(),
            dto.SeasonFolder,
            dto.MonitorMode?.Trim(),
            dto.SearchMissing,
            dto.SearchCutoff,
            dto.MinimumAvailability?.Trim(),
            dto.SearchForMovie
        );

        if (!ok) return NotFound(new { error = "app not found" });

        _activity.Add(null, "info", "arr", $"Arr app updated: {dto.Name ?? current.Name ?? current.Type}",
            dataJson: $"{{\"appId\":{id}}}");

        var updated = _repo.Get(id);
        return Ok(updated is not null ? ToResponseDto(updated) : new { id });
    }

    // DELETE /api/apps/{id}
    [HttpDelete("{id:long}")]
    public IActionResult Delete([FromRoute] long id)
    {
        var app = _repo.Get(id);
        if (app is null) return NotFound(new { error = "app not found" });

        var rows = _repo.Delete(id);
        if (rows == 0) return NotFound(new { error = "app not found" });

        _activity.Add(null, "info", "arr", $"Arr app deleted: {app.Name ?? app.Type}",
            dataJson: $"{{\"appId\":{id}}}");

        return NoContent();
    }

    // PUT /api/apps/{id}/enabled
    [HttpPut("{id:long}/enabled")]
    public async Task<IActionResult> SetEnabled([FromRoute] long id, [FromBody] ArrAppEnabledDto dto, CancellationToken ct)
    {
        var ok = _repo.SetEnabled(id, dto.Enabled);
        if (!ok) return NotFound(new { error = "app not found" });

        _activity.Add(null, "info", "arr",
            dto.Enabled ? "Arr app enabled" : "Arr app disabled",
            dataJson: $"{{\"appId\":{id},\"enabled\":{(dto.Enabled ? "true" : "false")}}}");

        // Trigger sync when app is enabled
        if (dto.Enabled)
        {
            try
            {
                await _syncService.SyncAppAsync(id, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Arr sync failed after enabling appId={AppId}", id);
                _activity.Add(null, "warn", "arr", "Arr app enabled but sync failed",
                    dataJson: $"{{\"appId\":{id}}}");
            }
        }

        return Ok(new { id, enabled = dto.Enabled });
    }

    // PUT /api/apps/{id}/default
    [HttpPut("{id:long}/default")]
    public IActionResult SetDefault([FromRoute] long id)
    {
        var ok = _repo.SetDefault(id);
        if (!ok) return NotFound(new { error = "app not found" });

        _activity.Add(null, "info", "arr", "Arr app set as default",
            dataJson: $"{{\"appId\":{id}}}");

        return Ok(new { id, isDefault = true });
    }

    // POST /api/apps/{id}/test
    [HttpPost("{id:long}/test")]
    public async Task<ActionResult<ArrAppTestResultDto>> TestById(
        [FromRoute] long id,
        CancellationToken ct)
    {
        var app = _repo.Get(id);
        if (app is null) return NotFound(new { error = "app not found" });

        return await TestConnection(app.Type, app.BaseUrl, app.ApiKeyEncrypted, ct);
    }

    // POST /api/apps/test
    [HttpPost("test")]
    public async Task<ActionResult<ArrAppTestResultDto>> Test(
        [FromBody] ArrAppTestRequestDto dto,
        [FromQuery] string type,
        CancellationToken ct)
    {
        var appType = (type ?? "").Trim().ToLowerInvariant();
        if (appType != "sonarr" && appType != "radarr")
            return BadRequest(new { error = "type query param must be 'sonarr' or 'radarr'" });

        if (!OutboundUrlGuard.TryNormalizeArrBaseUrl(dto.BaseUrl, out var baseUrl, out var baseUrlError))
            return BadRequest(new { error = baseUrlError });

        var apiKey = (dto.ApiKey ?? "").Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
            return BadRequest(new { error = "apiKey missing" });

        return await TestConnection(appType, baseUrl, apiKey, ct);
    }

    private async Task<ActionResult<ArrAppTestResultDto>> TestConnection(
        string type, string baseUrl, string apiKey, CancellationToken ct)
    {
        if (!OutboundUrlGuard.TryNormalizeArrBaseUrl(baseUrl, out var normalizedBaseUrl, out var baseUrlError))
            return BadRequest(new { error = baseUrlError });

        var sw = Stopwatch.StartNew();

        if (type == "sonarr")
        {
            var (ok, version, appName, error) = await _sonarr.TestConnectionAsync(normalizedBaseUrl, apiKey, ct);
            sw.Stop();
            return Ok(new ArrAppTestResultDto
            {
                Ok = ok,
                Error = error,
                LatencyMs = sw.ElapsedMilliseconds,
                Version = version,
                AppName = appName
            });
        }
        else
        {
            var (ok, version, appName, error) = await _radarr.TestConnectionAsync(normalizedBaseUrl, apiKey, ct);
            sw.Stop();
            return Ok(new ArrAppTestResultDto
            {
                Ok = ok,
                Error = error,
                LatencyMs = sw.ElapsedMilliseconds,
                Version = version,
                AppName = appName
            });
        }
    }

    // GET /api/apps/{id}/config
    [HttpGet("{id:long}/config")]
    public async Task<IActionResult> GetConfig([FromRoute] long id, CancellationToken ct)
    {
        var app = _repo.Get(id);
        if (app is null) return NotFound(new { error = "app not found" });

        try
        {
            if (app.Type == "sonarr")
            {
                var rootFolders = await _sonarr.GetRootFoldersAsync(app.BaseUrl, app.ApiKeyEncrypted, ct);
                var profiles = await _sonarr.GetQualityProfilesAsync(app.BaseUrl, app.ApiKeyEncrypted, ct);
                var tags = await _sonarr.GetTagsAsync(app.BaseUrl, app.ApiKeyEncrypted, ct);

                return Ok(new ArrConfigResponseDto
                {
                    RootFolders = rootFolders.Select(r => new ArrRootFolderDto
                    {
                        Id = r.Id,
                        Path = r.Path,
                        FreeSpace = r.FreeSpace
                    }).ToList(),
                    QualityProfiles = profiles.Select(p => new ArrQualityProfileDto
                    {
                        Id = p.Id,
                        Name = p.Name
                    }).ToList(),
                    Tags = tags.Select(t => new ArrTagDto
                    {
                        Id = t.Id,
                        Label = t.Label
                    }).ToList()
                });
            }
            else
            {
                var rootFolders = await _radarr.GetRootFoldersAsync(app.BaseUrl, app.ApiKeyEncrypted, ct);
                var profiles = await _radarr.GetQualityProfilesAsync(app.BaseUrl, app.ApiKeyEncrypted, ct);
                var tags = await _radarr.GetTagsAsync(app.BaseUrl, app.ApiKeyEncrypted, ct);

                return Ok(new ArrConfigResponseDto
                {
                    RootFolders = rootFolders.Select(r => new ArrRootFolderDto
                    {
                        Id = r.Id,
                        Path = r.Path,
                        FreeSpace = r.FreeSpace
                    }).ToList(),
                    QualityProfiles = profiles.Select(p => new ArrQualityProfileDto
                    {
                        Id = p.Id,
                        Name = p.Name
                    }).ToList(),
                    Tags = tags.Select(t => new ArrTagDto
                    {
                        Id = t.Id,
                        Label = t.Label
                    }).ToList()
                });
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Timed out while loading Arr app config for appId={AppId}", id);
            return StatusCode(504, new { error = "arr service timeout" });
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "Arr app config upstream request failed for appId={AppId}", id);
            return StatusCode(502, new { error = "arr service unavailable" });
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Arr app config returned invalid JSON for appId={AppId}", id);
            return StatusCode(502, new { error = "invalid arr response" });
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            _log.LogInformation(ex, "Arr app config request canceled for appId={AppId}", id);
            return BadRequest(new { error = "request canceled" });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load Arr app config for appId={AppId}", id);
            return StatusCode(500, new { error = "internal server error" });
        }
    }
}

public sealed class ArrAppEnabledDto
{
    public bool Enabled { get; set; }
}
