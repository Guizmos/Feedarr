// SourcesController.cs
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Feedarr.Api.Dtos.Sources;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services.Torznab;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Security;
using Microsoft.Extensions.Logging;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/sources")]
public sealed class SourcesController : ControllerBase
{
    private readonly TorznabClient _torznab;
    private readonly SourceRepository _sources;
    private readonly ProviderRepository _providers;
    private readonly ReleaseRepository _releases;
    private readonly ActivityRepository _activity;
    private readonly CategoryRecommendationService _caps;
    private readonly BackupExecutionCoordinator _backupCoordinator;
    private readonly SyncOrchestrationService _syncOrchestration;
    private readonly ILogger<SourcesController> _log;

    public SourcesController(
        TorznabClient torznab,
        SourceRepository sources,
        ProviderRepository providers,
        ReleaseRepository releases,
        ActivityRepository activity,
        CategoryRecommendationService caps,
        BackupExecutionCoordinator backupCoordinator,
        SyncOrchestrationService syncOrchestration,
        ILogger<SourcesController> log)
    {
        _torznab = torznab;
        _sources = sources;
        _providers = providers;
        _releases = releases;
        _activity = activity;
        _caps = caps;
        _backupCoordinator = backupCoordinator;
        _syncOrchestration = syncOrchestration;
        _log = log;
    }

    private static List<(int id, string name, bool isSub, int? parentId)> BuildFallbackCategories(IEnumerable<int?> ids)
    {
        return ids
            .Where(x => x.HasValue && x.Value > 0)
            .Select(x => x!.Value)
            .Distinct()
            .OrderBy(x => x)
            .Select(id => (id, $"Cat {id}", false, (int?)null))
            .ToList();
    }

    private async Task<bool> TryFallbackCategoriesFromRss(long sourceId, string url, string mode, string key, CancellationToken ct)
    {
        try
        {
            var (items, _) = await _torznab.FetchLatestAsync(url, mode, key, 100, ct);
            var fallback = BuildFallbackCategories(items.Select(x => x.CategoryId));

            if (fallback.Count > 0)
            {
                _sources.ReplaceCategories(sourceId, fallback);
                _activity.Add(sourceId, "warn", "source", "Caps empty - categories inferred from RSS (generic names)");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            var safeError = ErrorMessageSanitizer.ToOperationalMessage(ex, "caps/rss fallback failed");
            _activity.Add(sourceId, "error", "source", $"Caps failed and RSS fallback failed: {safeError}");
            return false;
        }
    }

    private static string? NormalizeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("#", StringComparison.Ordinal))
            trimmed = $"#{trimmed}";
        if (trimmed.Length != 7) return null;
        var hex = trimmed.AsSpan(1);
        foreach (var ch in hex)
        {
            var isHex = (ch >= '0' && ch <= '9') ||
                        (ch >= 'a' && ch <= 'f') ||
                        (ch >= 'A' && ch <= 'F');
            if (!isHex) return null;
        }
        return trimmed.ToLowerInvariant();
    }

    // GET /api/sources
    [HttpGet]
    public IActionResult List()
        => Ok(_sources.List());

    // GET /api/sources/{id}
    [HttpGet("{id:long}")]
    public IActionResult Get([FromRoute] long id)
    {
        var src = _sources.Get(id);
        if (src is null) return NotFound(new { error = "source not found" });

        return Ok(new
        {
            id = src.Id,
            name = src.Name,
            enabled = src.Enabled,
            torznabUrl = src.TorznabUrl,
            authMode = src.AuthMode,
            rssMode = src.RssMode,
            hasApiKey = !string.IsNullOrWhiteSpace(src.ApiKey),
            providerId = src.ProviderId,
            color = src.Color
        });
    }

    // POST /api/sources/test (test à la volée)
    [HttpPost("test")]
    public async Task<ActionResult<SourceTestResultDto>> Test([FromBody] SourceTestRequestDto req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var res = new SourceTestResultDto();

        try
        {
            var torznabUrl = (req.TorznabUrl ?? "").Trim();
            var apiKey = (req.ApiKey ?? "").Trim();
            var authMode = string.IsNullOrWhiteSpace(req.AuthMode) ? "query" : req.AuthMode.Trim().ToLowerInvariant();
            var limit = req.RssLimit <= 0 ? 50 : req.RssLimit;

            if (string.IsNullOrWhiteSpace(torznabUrl))
                return BadRequest(new { error = "torznabUrl missing" });

            var cats = await _torznab.FetchCapsAsync(torznabUrl, authMode, apiKey, ct);
            res.Caps.CategoriesTotal = cats.Count;
            res.Caps.CategoriesTop = cats.Take(10)
                .Select(c => new SourceTestResultDto.CapsInfo.Cat
                {
                    Id = c.id,
                    Name = c.name,
                    IsSub = c.isSub,
                    ParentId = c.parentId
                })
                .ToList();
            res.Caps.Categories = cats
                .Select(c => new SourceTestResultDto.CapsInfo.Cat
                {
                    Id = c.id,
                    Name = c.name,
                    IsSub = c.isSub,
                    ParentId = c.parentId
                })
                .ToList();

            var (items, usedMode) = await _torznab.FetchLatestAsync(torznabUrl, authMode, apiKey, limit, ct);

            res.Rss.ItemsCount = items.Count;
            res.Rss.FirstTitle = items.FirstOrDefault()?.Title;
            res.Rss.FirstPublishedAt = items.FirstOrDefault()?.PublishedAtTs;
            res.Rss.UsedMode = usedMode;

            res.Rss.Sample = items.Take(3).Select(x => new
            {
                x.Title,
                x.Seeders,
                x.Leechers,
                x.Grabs,
                x.SizeBytes,
                x.CategoryId,
                x.DownloadUrl
            }).ToList();

            res.Ok = true;
        }
        catch (Exception ex)
        {
            res.Ok = false;
            res.Error = ErrorMessageSanitizer.ToOperationalMessage(ex, "source test failed");
        }
        finally
        {
            sw.Stop();
            res.LatencyMs = sw.ElapsedMilliseconds;
        }

        return Ok(res);
    }

    // POST /api/sources/{id}/test (test d’une source enregistrée)
    [HttpPost("{id:long}/test")]
    public async Task<ActionResult<SourceTestResultDto>> TestById(
        [FromRoute] long id,
        [FromBody] SourceTestByIdDto? dto,
        CancellationToken ct)
    {
        var src = _sources.Get(id);
        if (src is null) return NotFound(new { error = "source not found" });

        var req = new SourceTestRequestDto
        {
            TorznabUrl = src.TorznabUrl,
            ApiKey = src.ApiKey ?? "",
            AuthMode = src.AuthMode,
            RssLimit = dto?.RssLimit ?? 50
        };

        return await Test(req, ct);
    }

    // POST /api/sources
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SourceCreateDto dto, CancellationToken ct)
    {
        var name = (dto.Name ?? "").Trim();
        var url = (dto.TorznabUrl ?? "").Trim();
        var key = (dto.ApiKey ?? "").Trim();
        var mode = string.IsNullOrWhiteSpace(dto.AuthMode) ? "query" : dto.AuthMode.Trim().ToLowerInvariant();
        if (mode != "header") mode = "query";
        long? providerId = dto.ProviderId is > 0 ? dto.ProviderId : null;
        string? color = null;
        if (!string.IsNullOrWhiteSpace(dto.Color))
        {
            color = NormalizeColor(dto.Color);
            if (color is null)
                return BadRequest(new { error = "color invalid" });
        }

        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { error = "name missing" });
        if (string.IsNullOrWhiteSpace(url)) return BadRequest(new { error = "torznabUrl missing" });

        var existingId = _sources.GetIdByTorznabUrl(url);
        if (existingId.HasValue)
            return Conflict(new { error = "torznabUrl already exists", id = existingId.Value });

        if (providerId.HasValue)
        {
            var provider = _providers.Get(providerId.Value);
            if (provider is null)
                return BadRequest(new { error = "provider not found" });
            if (string.IsNullOrWhiteSpace(key))
            {
                key = provider.ApiKey ?? "";
            }
        }

        if (string.IsNullOrWhiteSpace(key)) return BadRequest(new { error = "apiKey missing" });

        List<SourceRepository.SourceCategoryInput>? filtered = null;

        if (dto.Categories is not null && dto.Categories.Count > 0)
        {
            filtered = dto.Categories
                .Where(c => c.Id > 0 && !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => new SourceRepository.SourceCategoryInput
                {
                    Id = c.Id,
                    Name = c.Name.Trim(),
                    IsSub = c.IsSub,
                    ParentId = c.ParentId,
                    UnifiedKey = string.IsNullOrWhiteSpace(c.UnifiedKey) ? null : c.UnifiedKey.Trim(),
                    UnifiedLabel = string.IsNullOrWhiteSpace(c.UnifiedLabel) ? null : c.UnifiedLabel.Trim()
                })
                .ToList();

            if (filtered.Count == 0)
                return BadRequest(new { error = "categories missing" });
        }

        var id = _sources.Create(name, url, key, mode, providerId, color);

        if (filtered is not null)
        {
            _sources.ReplaceCategories(id, filtered);
        }
        else
        {
            // On essaie de charger les categories
            try
            {
                var cats = await _torznab.FetchCapsAsync(url, mode, key, ct);

                if (cats.Count > 0)
                {
                    _sources.ReplaceCategories(id, cats);
                }
                else
                {
                    var okFallback = await TryFallbackCategoriesFromRss(id, url, mode, key, ct);
                    if (!okFallback)
                        _activity.Add(id, "error", "source", "Caps returned 0 categories (apiKey? url?)");
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Caps refresh failed during source creation for sourceId={SourceId}", id);
                var safeError = ErrorMessageSanitizer.ToOperationalMessage(ex, "caps refresh failed");
                _activity.Add(id, "error", "source", $"Caps refresh failed on create: {safeError}");
                var okFallback = await TryFallbackCategoriesFromRss(id, url, mode, key, ct);
                if (!okFallback)
                {
                    _log.LogWarning("RSS fallback did not recover categories during source creation for sourceId={SourceId}", id);
                    _activity.Add(id, "error", "source", "Caps refresh failed and RSS fallback did not recover categories");
                }
            }
        }

        _activity.Add(id, "info", "source", $"Source added: {name}",
            dataJson: $"{{\"sourceId\":{id}}}");

        return Ok(new { id });
    }

    // POST /api/sources/{id}/sync
    [HttpPost("{id:long}/sync")]
    public async Task<IActionResult> Sync([FromRoute] long id, [FromQuery] bool? rssOnly, CancellationToken ct)
    {
        using var syncLease = _backupCoordinator.TryEnterSyncActivity("sources-sync-manual");
        if (syncLease is null)
            return Conflict(new { ok = false, error = "backup operation in progress" });

        var src = _sources.Get(id);
        if (src is null) return NotFound(new { error = "source not found" });

        var result = await _syncOrchestration.ExecuteManualSyncAsync(src, rssOnly ?? false, ct);
        if (!result.Ok)
            return StatusCode(500, new { ok = false, error = "internal server error" });

        return Ok(new
        {
            ok = true,
            usedMode = result.UsedMode,
            syncMode = result.SyncMode,
            itemsCount = result.ItemsCount,
            insertedNew = result.InsertedNew
        });
    }

    // PUT /api/sources/{id}/enabled
    [HttpPut("{id:long}/enabled")]
    public IActionResult SetEnabled([FromRoute] long id, [FromBody] SourceEnabledDto dto)
    {
        var ok = _sources.SetEnabled(id, dto.Enabled);
        if (!ok) return NotFound(new { error = "source not found" });

        _activity.Add(id, "info", "source",
            dto.Enabled ? "Source enabled" : "Source disabled",
            dataJson: $"{{\"enabled\":{(dto.Enabled ? "true" : "false")}}}");

        return Ok(new { id, enabled = dto.Enabled });
    }

    // PUT /api/sources/{id}
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update([FromRoute] long id, [FromBody] SourceUpdateDto dto, CancellationToken ct)
    {
        var current = _sources.Get(id);
        if (current is null) return NotFound(new { error = "source not found" });

        var name = string.IsNullOrWhiteSpace(dto.Name) ? current.Name : dto.Name!.Trim();
        var url = string.IsNullOrWhiteSpace(dto.TorznabUrl) ? current.TorznabUrl : dto.TorznabUrl!.Trim();

        var mode = string.IsNullOrWhiteSpace(dto.AuthMode) ? current.AuthMode : dto.AuthMode!.Trim().ToLowerInvariant();
        if (mode != "header") mode = "query";

        // apiKey: si null/empty => on ne change pas
        string? apiKeyMaybe = null;
        if (!string.IsNullOrWhiteSpace(dto.ApiKey))
            apiKeyMaybe = dto.ApiKey.Trim();

        string? colorMaybe = null;
        if (!string.IsNullOrWhiteSpace(dto.Color))
        {
            colorMaybe = NormalizeColor(dto.Color);
            if (colorMaybe is null)
                return BadRequest(new { error = "color invalid" });
        }

        if (!string.Equals(url, current.TorznabUrl, StringComparison.Ordinal))
        {
            var existingId = _sources.GetIdByTorznabUrl(url);
            if (existingId.HasValue && existingId.Value != id)
                return Conflict(new { error = "torznabUrl already exists", id = existingId.Value });
        }

        var ok = _sources.Update(id, name, url, mode, apiKeyMaybe, colorMaybe);
        if (!ok) return NotFound(new { error = "source not found" });
        _caps.InvalidateSource(id);

        var currentUrl = current.TorznabUrl;
        var currentMode = current.AuthMode;
        var shouldRefreshCaps =
            !string.Equals(url, currentUrl, StringComparison.Ordinal) ||
            !string.Equals(mode, currentMode, StringComparison.Ordinal) ||
            apiKeyMaybe is not null;

        var existingMap = _sources.GetUnifiedCategoryMap(id);

        // Refresh categories MAIS:
        // ✅ si caps revient vide, on ne wipe pas les catégories existantes
        var keyForCaps = apiKeyMaybe ?? current.ApiKey ?? "";

        try
        {
            if (!shouldRefreshCaps)
            {
                _activity.Add(id, "info", "source", "Source updated (no caps refresh)");
                return Ok(new { id, name, torznabUrl = url, authMode = mode });
            }

            var cats = await _torznab.FetchCapsAsync(url, mode, keyForCaps, ct);

            if (cats.Count > 0)
            {
                if (existingMap.Count > 0)
                {
                    var mapped = cats.Select(c => new SourceRepository.SourceCategoryInput
                    {
                        Id = c.id,
                        Name = c.name,
                        IsSub = c.isSub,
                        ParentId = c.parentId,
                        UnifiedKey = existingMap.TryGetValue(c.id, out var entry) ? entry.key : null,
                        UnifiedLabel = existingMap.TryGetValue(c.id, out var entry2) ? entry2.label : null
                    });
                    _sources.ReplaceCategories(id, mapped);
                }
                else
                {
                    _sources.ReplaceCategories(id, cats);
                }
            }
            else
            {
                var okFallback = await TryFallbackCategoriesFromRss(id, url, mode, keyForCaps, ct);
                if (!okFallback)
                    _activity.Add(id, "error", "source",
                        "Caps returned 0 categories - keeping previous categories (check apiKey/url)");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Caps refresh failed during source update for sourceId={SourceId}", id);
            var safeError = ErrorMessageSanitizer.ToOperationalMessage(ex, "caps refresh failed");
            _activity.Add(id, "error", "source", $"Caps refresh failed: {safeError}");
            var okFallback = await TryFallbackCategoriesFromRss(id, url, mode, keyForCaps, ct);
            if (!okFallback)
            {
                _log.LogWarning("RSS fallback did not recover categories during source update for sourceId={SourceId}", id);
                _activity.Add(id, "error", "source", "Caps refresh failed and RSS fallback did not recover categories");
            }
        }

        _activity.Add(id, "info", "source", $"Source updated: {name}",
            dataJson: $"{{\"sourceId\":{id}}}");

        return Ok(new { id, name, torznabUrl = url, authMode = mode });
    }

    // PUT /api/sources/{id}/categories
    [HttpPut("{id:long}/categories")]
    public IActionResult UpdateCategories([FromRoute] long id, [FromBody] SourceCategoriesUpdateDto dto)
    {
        var src = _sources.Get(id);
        if (src is null) return NotFound(new { error = "source not found" });

        _log.LogWarning("Legacy endpoint PUT /api/sources/{SourceId}/categories called; category mappings remain source of truth.", id);

        var existingMap = _sources.GetUnifiedCategoryMap(id);

        var filtered = (dto?.Categories ?? new List<SourceCategorySelectionDto>())
            .Where(c => c.Id > 0 && !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => new SourceRepository.SourceCategoryInput
            {
                Id = c.Id,
                Name = c.Name.Trim(),
                IsSub = c.IsSub,
                ParentId = c.ParentId,
                UnifiedKey = existingMap.TryGetValue(c.Id, out var entry)
                    ? entry.key
                    : (string.IsNullOrWhiteSpace(c.UnifiedKey) ? null : c.UnifiedKey.Trim()),
                UnifiedLabel = existingMap.TryGetValue(c.Id, out var entry2)
                    ? entry2.label
                    : (string.IsNullOrWhiteSpace(c.UnifiedLabel) ? null : c.UnifiedLabel.Trim())
            })
            .ToList();

        if (filtered.Count == 0)
            return BadRequest(new { error = "categories missing" });

        _sources.ReplaceCategories(id, filtered);
        _caps.InvalidateSource(id);
        _activity.Add(id, "info", "source", $"Source categories updated: {src.Name}",
            dataJson: $"{{\"sourceId\":{id},\"categories\":{filtered.Count}}}");

        return Ok(new { id, categories = filtered.Count });
    }

    // GET /api/sources/{id}/category-mappings
    [HttpGet("{id:long}/category-mappings")]
    public IActionResult GetCategoryMappings([FromRoute] long id)
    {
        var src = _sources.Get(id);
        if (src is null) return NotFound(new { error = "source not found" });

        var mappings = _sources.GetCategoryMappings(id)
            .Select(m => new SourceCategoryMappingDto
            {
                CatId = m.CatId,
                GroupKey = m.GroupKey,
                GroupLabel = m.GroupLabel
            })
            .ToList();

        return Ok(mappings);
    }

    // PATCH /api/sources/{id}/category-mappings
    [HttpPatch("{id:long}/category-mappings")]
    public IActionResult PatchCategoryMappings([FromRoute] long id, [FromBody] SourceCategoryMappingsPatchDto? dto)
    {
        var src = _sources.Get(id);
        if (src is null) return NotFound(new { error = "source not found" });

        var rawMappings = (dto?.Mappings ?? new List<SourceCategoryMappingPatchItemDto>()).ToList();
        if (rawMappings.Count == 0)
            return BadRequest(new { error = "mappings missing" });

        foreach (var mapping in rawMappings)
        {
            if (mapping.CatId <= 0)
                return BadRequest(new { error = $"invalid catId: {mapping.CatId}" });

            if (!string.IsNullOrWhiteSpace(mapping.GroupKey) &&
                !SourceRepository.IsAllowedGroupKey(mapping.GroupKey))
            {
                return BadRequest(new { error = $"invalid groupKey for catId={mapping.CatId}" });
            }
        }

        var changed = _sources.PatchCategoryMappings(
            id,
            rawMappings.Select(m => new SourceRepository.SourceCategoryMappingPatch
            {
                CatId = m.CatId,
                GroupKey = string.IsNullOrWhiteSpace(m.GroupKey) ? null : m.GroupKey!.Trim()
            }));

        _caps.InvalidateSource(id);
        _activity.Add(id, "info", "source", $"Source category mappings patched: {src.Name}",
            dataJson: $"{{\"sourceId\":{id},\"changed\":{changed},\"patched\":{rawMappings.Count}}}");

        var updated = _sources.GetCategoryMappings(id)
            .Select(m => new SourceCategoryMappingDto
            {
                CatId = m.CatId,
                GroupKey = m.GroupKey,
                GroupLabel = m.GroupLabel
            })
            .ToList();

        return Ok(new { changed, mappings = updated });
    }

    // POST /api/sources/{id}/reclassify
    [HttpPost("{id:long}/reclassify")]
    public IActionResult ReclassifySource([FromRoute] long id, [FromQuery] int batchSize = 200)
    {
        var src = _sources.Get(id);
        if (src is null) return NotFound(new { error = "source not found" });

        try
        {
            var (processed, updated, markedRebind) = _releases.ReprocessCategoriesForSource(id);
            var (rebindProcessed, rebound) = _releases.RebindEntitiesForSource(id, batchSize);

            _activity.Add(id, "info", "maintenance", "Source reclassify completed",
                dataJson: $"{{\"sourceId\":{id},\"processed\":{processed},\"updated\":{updated},\"markedRebind\":{markedRebind},\"rebindProcessed\":{rebindProcessed},\"rebound\":{rebound}}}");

            return Ok(new
            {
                ok = true,
                sourceId = id,
                processed,
                updated,
                markedRebind,
                rebindProcessed,
                rebound
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Source reclassify failed for sourceId={SourceId}", id);
            return StatusCode(500, new { ok = false, error = "internal server error" });
        }
    }

    // DELETE /api/sources/{id}
    [HttpDelete("{id:long}")]
    public IActionResult Delete([FromRoute] long id)
    {
        var src = _sources.Get(id);
        if (src is null) return NotFound(new { error = "source not found" });

        var rows = _sources.Delete(id);
        if (rows == 0) return NotFound(new { error = "source not found" });
        _caps.InvalidateSource(id);

        _activity.Add(id, "info", "source", $"Source deleted: {src.Name}",
            dataJson: $"{{\"sourceId\":{id}}}");

        return NoContent(); // 204
    }
}
