using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;
using Feedarr.Api.Dtos.Categories;
using Feedarr.Api.Dtos.Providers;
using Feedarr.Api.Services.Categories;
using Microsoft.Extensions.Caching.Memory;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/categories")]
public sealed class CategoriesController : ControllerBase
{
    private sealed class UnifiedCategoryCountRow
    {
        public string? UnifiedCategory { get; set; }
        public int Count { get; set; }
    }

    private sealed class HeuristicCategoryCountRow
    {
        public string? CategoryName { get; set; }
        public int Count { get; set; }
    }

    private readonly Db _db;
    private readonly UnifiedCategoryService _unified;
    private readonly CategoryRecommendationService _caps;
    private readonly ProviderRepository _providers;
    private readonly IMemoryCache _cache;
    public CategoriesController(
        Db db,
        UnifiedCategoryService unified,
        CategoryRecommendationService caps,
        ProviderRepository providers,
        IMemoryCache cache)
    {
        _db = db;
        _unified = unified;
        _caps = caps;
        _providers = providers;
        _cache = cache;
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
        const string cacheKey = "categories:stats:v2";
        if (_cache.TryGetValue<object>(cacheKey, out var cached) && cached is not null)
            return Ok(cached);

        using var conn = _db.Open();
        var hasCategories = conn.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='source_categories';"
        ) > 0;

        if (!hasCategories)
        {
            return Ok(new { stats = Array.Empty<object>() });
        }

        var counts = new Dictionary<string, (string label, int count)>(StringComparer.OrdinalIgnoreCase);

        var mappedRows = conn.Query<(string UnifiedKey, string UnifiedLabel, int Count)>(
            """
            SELECT
              lower(sc.unified_key) as UnifiedKey,
              COALESCE(NULLIF(sc.unified_label, ''), sc.unified_key) as UnifiedLabel,
              COUNT(1) as Count
            FROM releases r
            INNER JOIN source_categories sc
              ON sc.source_id = r.source_id AND sc.cat_id = r.category_id
            WHERE sc.unified_key IS NOT NULL AND sc.unified_key <> ''
            GROUP BY lower(sc.unified_key), COALESCE(NULLIF(sc.unified_label, ''), sc.unified_key);
            """
        );

        foreach (var row in mappedRows)
        {
            var key = row.UnifiedKey?.Trim();
            if (string.IsNullOrWhiteSpace(key)) continue;
            var label = string.IsNullOrWhiteSpace(row.UnifiedLabel) ? key : row.UnifiedLabel;
            if (counts.TryGetValue(key, out var current))
                counts[key] = (current.label, current.count + row.Count);
            else
                counts[key] = (label, row.Count);
        }

        var unresolvedUnifiedValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unifiedRows = conn.Query<UnifiedCategoryCountRow>(
            """
            SELECT
              r.unified_category as UnifiedCategory,
              COUNT(1) as Count
            FROM releases r
            LEFT JOIN source_categories sc
              ON sc.source_id = r.source_id AND sc.cat_id = r.category_id
            WHERE (sc.unified_key IS NULL OR sc.unified_key = '')
            GROUP BY r.unified_category;
            """
        );

        foreach (var row in unifiedRows)
        {
            if (UnifiedCategoryMappings.TryParse(row.UnifiedCategory, out var unifiedCategory) &&
                unifiedCategory != UnifiedCategory.Autre)
            {
                var key = UnifiedCategoryMappings.ToKey(unifiedCategory);
                var label = UnifiedCategoryMappings.ToLabel(unifiedCategory);
                if (counts.TryGetValue(key, out var current))
                    counts[key] = (current.label, current.count + row.Count);
                else
                    counts[key] = (label, row.Count);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(row.UnifiedCategory))
                    unresolvedUnifiedValues.Add(row.UnifiedCategory);
            }
        }

        var unresolvedRows = conn.Query<HeuristicCategoryCountRow>(
            """
            SELECT
              sc.name as CategoryName,
              COUNT(1) as Count
            FROM releases r
            LEFT JOIN source_categories sc
              ON sc.source_id = r.source_id AND sc.cat_id = r.category_id
            WHERE (sc.unified_key IS NULL OR sc.unified_key = '')
              AND (
                r.unified_category IS NULL OR
                r.unified_category = '' OR
                lower(r.unified_category) = 'autre' OR
                r.unified_category IN @invalidUnified
              )
            GROUP BY sc.name;
            """,
            new { invalidUnified = unresolvedUnifiedValues.Count == 0 ? new[] { "__none__" } : unresolvedUnifiedValues.ToArray() }
        );

        foreach (var row in unresolvedRows)
        {
            var unified = _unified.Get(row.CategoryName, null);
            if (unified is null) continue;
            var key = unified.Key;
            var label = unified.Label;

            if (counts.TryGetValue(key, out var current))
                counts[key] = (current.label, current.count + row.Count);
            else
                counts[key] = (label ?? key, row.Count);
        }

        var stats = counts
            .Select(kvp => new { key = kvp.Key, name = kvp.Value.label, count = kvp.Value.count })
            .OrderByDescending(x => x.count)
            .ToList();
        var payload = new { stats };
        _cache.Set(cacheKey, payload, TimeSpan.FromSeconds(30));
        return Ok(payload);
    }
}
