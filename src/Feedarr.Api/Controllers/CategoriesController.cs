using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;
using Feedarr.Api.Dtos.Categories;
using Feedarr.Api.Dtos.Providers;
using Feedarr.Api.Services.Categories;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/categories")]
public sealed class CategoriesController : ControllerBase
{
    private readonly Db _db;
    private readonly UnifiedCategoryService _unified;
    private readonly CategoryRecommendationService _caps;
    private readonly ProviderRepository _providers;
    public CategoriesController(
        Db db,
        UnifiedCategoryService unified,
        CategoryRecommendationService caps,
        ProviderRepository providers)
    {
        _db = db;
        _unified = unified;
        _caps = caps;
        _providers = providers;
    }

    [HttpGet("caps")]
    public async Task<ActionResult<CapsCategoriesResponseDto>> Caps(
        [FromQuery] CapsCategoriesRequestDto? dto,
        [FromQuery] int? debug,
        CancellationToken ct)
    {
        var payload = dto ?? new CapsCategoriesRequestDto();
        var res = await _caps.GetDecoratedCapsCategoriesAsync(payload, ct);
        if (debug != 1)
        {
            foreach (var cat in res.Categories)
            {
                cat.Reason = null;
                cat.Score = null;
            }
        }
        return Ok(res);
    }

    [HttpPost("caps")]
    public async Task<ActionResult<CapsCategoriesResponseDto>> CapsLegacy([FromBody] CapsCategoriesRequestDto? dto, CancellationToken ct)
    {
        var payload = dto ?? new CapsCategoriesRequestDto();
        var res = await _caps.GetDecoratedCapsCategoriesAsync(payload, ct);
        foreach (var cat in res.Categories)
        {
            cat.Reason = null;
            cat.Score = null;
        }
        return Ok(res);
    }

    [HttpPost("caps/provider")]
    public async Task<ActionResult<CapsCategoriesResponseDto>> CapsProvider([FromBody] ProviderCapsRequestDto? dto, CancellationToken ct)
    {
        var payload = dto ?? new ProviderCapsRequestDto();
        if (payload.ProviderId <= 0)
            return Problem(title: "providerId missing", statusCode: StatusCodes.Status400BadRequest);

        var provider = _providers.Get(payload.ProviderId);
        if (provider is null)
            return Problem(title: "provider not found", statusCode: StatusCodes.Status404NotFound);

        var type = provider.Type;
        if (!type.Equals("jackett", StringComparison.OrdinalIgnoreCase) &&
            !type.Equals("prowlarr", StringComparison.OrdinalIgnoreCase))
            return Problem(title: "provider type not supported", statusCode: StatusCodes.Status501NotImplemented);

        var baseUrl = provider.BaseUrl;
        var apiKey = provider.ApiKey ?? "";
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
            return Problem(title: "provider configuration incomplete", statusCode: StatusCodes.Status400BadRequest);

        var torznabUrl = (payload.TorznabUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(torznabUrl) && !string.IsNullOrWhiteSpace(payload.IndexerId))
        {
            var baseTrim = baseUrl.TrimEnd('/');
            if (type.Equals("prowlarr", StringComparison.OrdinalIgnoreCase))
            {
                torznabUrl = $"{baseTrim}/{payload.IndexerId.Trim()}/api";
            }
            else
            {
                torznabUrl = $"{baseTrim}/api/v2.0/indexers/{payload.IndexerId.Trim()}/results/torznab/";
            }
        }

        if (string.IsNullOrWhiteSpace(torznabUrl))
            return Problem(title: "torznabUrl missing", statusCode: StatusCodes.Status400BadRequest);

        var req = new CapsCategoriesRequestDto
        {
            TorznabUrl = torznabUrl,
            ApiKey = apiKey,
            AuthMode = "query",
            IndexerName = payload.IndexerName
        };

        var res = await _caps.GetDecoratedCapsCategoriesAsync(req, ct);
        foreach (var cat in res.Categories)
        {
            cat.Reason = null;
            cat.Score = null;
        }
        return Ok(res);
    }

    [HttpGet("{sourceId:long}")]
    public IActionResult List([FromRoute] long sourceId)
    {
        using var conn = _db.Open();

        var rows = conn.Query(
            """
            SELECT cat_id as id, name, parent_cat_id as parentId, is_sub as isSub,
                   unified_key as unifiedKey, unified_label as unifiedLabel
            FROM source_categories
            WHERE source_id = @sid
            ORDER BY id ASC;
            """,
            new { sid = sourceId }
        );

        return Ok(rows);
    }

    // GET /api/categories/stats
    [HttpGet("stats")]
    public IActionResult Stats()
    {
        using var conn = _db.Open();
        var hasCategories = conn.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='source_categories';"
        ) > 0;

        if (!hasCategories)
        {
            return Ok(new { stats = Array.Empty<object>() });
        }
        var rows = conn.Query(
            """
            SELECT
              r.title as title,
              r.title_clean as titleClean,
              r.category_id as categoryId,
              r.unified_category as unifiedCategory,
              sc.name as categoryName,
              sc.unified_key as unifiedKey,
              sc.unified_label as unifiedLabel
            FROM releases r
            LEFT JOIN source_categories sc
              ON sc.source_id = r.source_id AND sc.cat_id = r.category_id;
            """
        );

        var counts = new Dictionary<string, (string label, int count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            string? unifiedKey = row.unifiedKey;
            string? unifiedLabel = row.unifiedLabel;
            string? title = row.title ?? row.titleClean;
            string? categoryName = row.categoryName;
            var unifiedCategoryValue = (string?)row.unifiedCategory;
            var hasMappedKey = !string.IsNullOrWhiteSpace(unifiedKey);
            if (!hasMappedKey &&
                UnifiedCategoryMappings.TryParse(unifiedCategoryValue, out var unifiedCategory) &&
                unifiedCategory != UnifiedCategory.Autre)
            {
                unifiedKey = UnifiedCategoryMappings.ToKey(unifiedCategory);
                unifiedLabel = UnifiedCategoryMappings.ToLabel(unifiedCategory);
            }
            else
            {
                var unified = _unified.Get(categoryName, title);
                var overrideKey = unified?.Key is "shows" or "spectacle";

                if (!hasMappedKey && (overrideKey || string.IsNullOrWhiteSpace(unifiedKey)))
                {
                    if (unified is null) continue;
                    unifiedKey = unified.Key;
                    unifiedLabel = unified.Label;
                }
            }

            if (string.IsNullOrWhiteSpace(unifiedKey)) continue;

            if (counts.TryGetValue(unifiedKey, out var current))
            {
                counts[unifiedKey] = (current.label, current.count + 1);
            }
            else
            {
                counts[unifiedKey] = (unifiedLabel ?? unifiedKey, 1);
            }
        }

        var stats = counts
            .Select(kvp => new { key = kvp.Key, name = kvp.Value.label, count = kvp.Value.count })
            .OrderByDescending(x => x.count)
            .ToList();

        return Ok(new { stats });
    }
}
