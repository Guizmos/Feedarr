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
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/categories")]
public sealed class CategoriesController : ControllerBase
{
    private readonly Db _db;
    private readonly CategoryRecommendationService _caps;
    private readonly ProviderRepository _providers;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CategoriesController> _log;

    public CategoriesController(
        Db db,
        CategoryRecommendationService caps,
        ProviderRepository providers,
        IMemoryCache cache,
        ILogger<CategoriesController> log)
    {
        _db = db;
        _caps = caps;
        _providers = providers;
        _cache = cache;
        _log = log;
    }

    /// <summary>[DEBUG] Log les métriques de la réponse caps.</summary>
    private void LogCapsDebug(string endpoint, CapsCategoriesRequestDto req, CapsCategoriesResponseDto res)
    {
        var highIds = res.Categories.Where(c => c.Id >= 10000).Select(c => c.Id).Take(10).ToList();
        var supportedStandard = res.Categories.Count(c => c.IsStandard && c.IsSupported);
        var unsupportedStandard = res.Categories.Count(c => c.IsStandard && !c.IsSupported);
        _log.LogInformation(
            "[CAPS-DEBUG] endpoint={E} includeStandardCatalog={ISC} includeSpecific={IS} totalCats={TC} supportedStandard={SS} unsupportedStandard={US} highIds={HI} sampleHighIds={SH}",
            endpoint, req.IncludeStandardCatalog, req.IncludeSpecific, res.Categories.Count,
            supportedStandard, unsupportedStandard,
            highIds.Count, string.Join(",", highIds));
    }

    [HttpGet("caps")]
    public async Task<ActionResult<CapsCategoriesResponseDto>> Caps(
        [FromQuery] CapsCategoriesRequestDto? dto,
        [FromQuery] int? debug,
        CancellationToken ct)
    {
        var payload = dto ?? new CapsCategoriesRequestDto();
        var res = await _caps.GetDecoratedCapsCategoriesAsync(payload, ct);
        LogCapsDebug("GET caps", payload, res);
        return Ok(res);
    }

    [HttpPost("caps")]
    public async Task<ActionResult<CapsCategoriesResponseDto>> CapsLegacy([FromBody] CapsCategoriesRequestDto? dto, CancellationToken ct)
    {
        var payload = dto ?? new CapsCategoriesRequestDto();
        var res = await _caps.GetDecoratedCapsCategoriesAsync(payload, ct);
        LogCapsDebug("POST caps", payload, res);
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
            TorznabUrl   = torznabUrl,
            ApiKey       = apiKey,
            AuthMode     = "query",
            IndexerName  = payload.IndexerName,
            IncludeStandardCatalog = payload.IncludeStandardCatalog,
            IncludeSpecific = payload.IncludeSpecific
        };

        var res = await _caps.GetDecoratedCapsCategoriesAsync(req, ct);
        LogCapsDebug("POST caps/provider", req, res);
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
    // Agrégation directe sur releases.unified_category (champ pré-calculé, toujours cohérent).
    // Remplace l'ancienne version basée sur releases.category_id + JOIN source_categories
    // qui produisait des stats incohérentes quand unified_category et category_id divergeaient.
    [EnableRateLimiting("stats-heavy")]
    [HttpGet("stats")]
    public IActionResult Stats()
    {
        const string cacheKey = "categories:stats:v3";
        if (_cache.TryGetValue<object>(cacheKey, out var cached) && cached is not null)
            return Ok(cached);

        using var conn = _db.Open();

        var rows = conn.Query<(string? UnifiedCategory, int Count)>(
            """
            SELECT unified_category AS UnifiedCategory, COUNT(1) AS Count
            FROM releases
            WHERE unified_category IS NOT NULL
              AND unified_category <> ''
              AND unified_category <> 'Autre'
            GROUP BY unified_category
            ORDER BY Count DESC;
            """
        );

        var counts = new Dictionary<string, (string label, int count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!UnifiedCategoryMappings.TryParse(row.UnifiedCategory, out var cat) ||
                cat == UnifiedCategory.Autre)
                continue;

            var key   = UnifiedCategoryMappings.ToKey(cat);
            var label = UnifiedCategoryMappings.ToLabel(cat);
            if (counts.TryGetValue(key, out var current))
                counts[key] = (current.label, current.count + row.Count);
            else
                counts[key] = (label, row.Count);
        }

        var stats   = counts
            .Select(kvp => new { key = kvp.Key, name = kvp.Value.label, count = kvp.Value.count })
            .OrderByDescending(x => x.count)
            .ToList();
        var payload = new { stats };
        _cache.Set(cacheKey, payload, TimeSpan.FromSeconds(30));
        return Ok(payload);
    }
}
