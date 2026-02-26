using System.Security.Cryptography;
using System.Text;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Dtos.Categories;
using Feedarr.Api.Services.Torznab;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Feedarr.Api.Services.Categories;

/// <summary>
/// Retourne une vue "catégories brutes" pour la configuration des indexeurs:
/// - catalogue standard complet (1000-8999) avec support/non-support
/// - overlay des catégories spécifiques provider (>=10000)
/// </summary>
public sealed class CategoryRecommendationService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private readonly SourceRepository _sources;
    private readonly TorznabClient _torznab;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CategoryRecommendationService> _log;

    public CategoryRecommendationService(
        SourceRepository sources,
        TorznabClient torznab,
        IMemoryCache cache,
        ILogger<CategoryRecommendationService> log)
    {
        _sources = sources;
        _torznab = torznab;
        _cache = cache;
        _log = log;
    }

    public void InvalidateSource(long sourceId)
    {
        var key = $"caps:source:{sourceId}";
        _cache.Remove(key);
    }

    public async Task<CapsCategoriesResponseDto> GetDecoratedCapsCategoriesAsync(
        CapsCategoriesRequestDto req,
        CancellationToken ct)
    {
        var warnings = new List<string>();

        long? sourceId = req.SourceId;
        string torznabUrl = (req.TorznabUrl ?? "").Trim();
        string apiKey = (req.ApiKey ?? "").Trim();
        string authMode = (req.AuthMode ?? "query").Trim().ToLowerInvariant();
        if (authMode != "header") authMode = "query";

        if (sourceId.HasValue && sourceId.Value > 0)
        {
            var src = _sources.Get(sourceId.Value);
            if (src is null)
            {
                return new CapsCategoriesResponseDto
                {
                    Categories = new List<CapsCategoryDto>(),
                    Warnings = new List<string> { "Source introuvable." }
                };
            }

            torznabUrl = Convert.ToString(src.TorznabUrl) ?? torznabUrl;
            apiKey = Convert.ToString(src.ApiKey) ?? apiKey;
            authMode = Convert.ToString(src.AuthMode) ?? authMode;
            if (authMode != "header") authMode = "query";
        }

        Dictionary<int, string> capsById;
        HashSet<int> supportedIds;

        if (string.IsNullOrWhiteSpace(torznabUrl))
        {
            warnings.Add("Torznab URL manquante.");
            (capsById, supportedIds) = BuildFallbackFromStoredIds(sourceId);
        }
        else
        {
            var rawCaps = await GetCapsCategoriesAsync(torznabUrl, authMode, apiKey, sourceId, warnings, ct);
            capsById = BuildCapsDictionary(rawCaps);
            supportedIds = capsById.Keys.ToHashSet();

            if (supportedIds.Count == 0)
            {
                var (fallbackCapsById, fallbackSupported) = BuildFallbackFromStoredIds(sourceId);
                if (fallbackSupported.Count > 0 &&
                    !warnings.Any(w => w.Contains("Caps", StringComparison.OrdinalIgnoreCase)))
                {
                    warnings.Add("Caps indisponible. Catégories stockées renvoyées.");
                }

                capsById = fallbackCapsById;
                supportedIds = fallbackSupported;
            }
        }

        var assignedById = sourceId.HasValue && sourceId.Value > 0
            ? _sources.GetCategoryMappingMap(sourceId.Value)
            : new Dictionary<int, (string key, string label)>();

        return new CapsCategoriesResponseDto
        {
            Categories = BuildFlatCategories(
                capsById,
                supportedIds,
                assignedById,
                includeStandardCatalog: req.IncludeStandardCatalog,
                includeSpecific: req.IncludeSpecific),
            Warnings = warnings
        };
    }

    private static Dictionary<int, string> BuildCapsDictionary(IEnumerable<RawCategory> rawCaps)
    {
        var map = new Dictionary<int, string>();

        foreach (var cat in rawCaps)
        {
            if (cat.Id <= 0) continue;
            if (map.ContainsKey(cat.Id)) continue;

            var name = string.IsNullOrWhiteSpace(cat.Name) ? $"Cat {cat.Id}" : cat.Name.Trim();
            map[cat.Id] = name;
        }

        return map;
    }

    private (Dictionary<int, string> capsById, HashSet<int> supportedIds) BuildFallbackFromStoredIds(long? sourceId)
    {
        var byId = new Dictionary<int, string>();
        var supported = new HashSet<int>();

        if (!sourceId.HasValue || sourceId.Value <= 0)
            return (byId, supported);

        var storedIds = _sources.GetActiveCategoryIds(sourceId.Value);
        foreach (var id in storedIds.Where(id => id > 0).Distinct())
        {
            supported.Add(id);
            if (StandardCategoryCatalog.TryGetStandardName(id, out var standardName))
                byId[id] = standardName;
            else
                byId[id] = $"Cat {id}";
        }

        return (byId, supported);
    }

    private static List<CapsCategoryDto> BuildFlatCategories(
        IReadOnlyDictionary<int, string> capsById,
        IReadOnlyCollection<int> supportedIds,
        IReadOnlyDictionary<int, (string key, string label)> assignedById,
        bool includeStandardCatalog,
        bool includeSpecific)
    {
        var supportedSet = supportedIds
            .Where(id => id > 0)
            .ToHashSet();

        var result = new List<CapsCategoryDto>();

        if (includeStandardCatalog)
        {
            foreach (var cat in StandardCategoryCatalog.GetAllStandard().OrderBy(c => c.Id))
            {
                result.Add(new CapsCategoryDto
                {
                    Id = cat.Id,
                    Name = cat.Name,
                    IsStandard = true,
                    IsSupported = supportedSet.Contains(cat.Id),
                    AssignedGroupKey = assignedById.TryGetValue(cat.Id, out var assigned) ? assigned.key : null,
                    AssignedGroupLabel = assignedById.TryGetValue(cat.Id, out var assignedLabel) ? assignedLabel.label : null,
                    IsAssigned = assignedById.ContainsKey(cat.Id)
                });
            }
        }
        else
        {
            var standardIds = supportedSet
                .Where(StandardCategoryCatalog.IsStandardId)
                .Concat(assignedById.Keys.Where(StandardCategoryCatalog.IsStandardId))
                .Distinct()
                .OrderBy(id => id);

            foreach (var id in standardIds)
            {
                var name = StandardCategoryCatalog.TryGetStandardName(id, out var standardName)
                    ? standardName
                    : (capsById.TryGetValue(id, out var capsName) ? capsName : $"Cat {id}");

                result.Add(new CapsCategoryDto
                {
                    Id = id,
                    Name = name,
                    IsStandard = true,
                    IsSupported = supportedSet.Contains(id),
                    AssignedGroupKey = assignedById.TryGetValue(id, out var assigned) ? assigned.key : null,
                    AssignedGroupLabel = assignedById.TryGetValue(id, out var assignedLabel) ? assignedLabel.label : null,
                    IsAssigned = assignedById.ContainsKey(id)
                });
            }
        }

        if (includeSpecific)
        {
            var specificIds = capsById.Keys
                .Where(id => !StandardCategoryCatalog.IsStandardId(id))
                .Concat(assignedById.Keys.Where(id => !StandardCategoryCatalog.IsStandardId(id)))
                .Distinct()
                .OrderBy(id => id);

            foreach (var id in specificIds)
            {
                result.Add(new CapsCategoryDto
                {
                    Id = id,
                    Name = capsById.TryGetValue(id, out var name) ? name : $"Cat {id}",
                    IsStandard = false,
                    IsSupported = true,
                    AssignedGroupKey = assignedById.TryGetValue(id, out var assigned) ? assigned.key : null,
                    AssignedGroupLabel = assignedById.TryGetValue(id, out var assignedLabel) ? assignedLabel.label : null,
                    IsAssigned = assignedById.ContainsKey(id)
                });
            }
        }

        return result
            .OrderBy(c => c.Id)
            .ToList();
    }

    private async Task<List<RawCategory>> GetCapsCategoriesAsync(
        string torznabUrl,
        string authMode,
        string apiKey,
        long? sourceId,
        List<string> warnings,
        CancellationToken ct)
    {
        try
        {
            var fingerprint = BuildFingerprint(torznabUrl, authMode, apiKey);
            var cacheKey = BuildCacheKey(sourceId, fingerprint);

            if (_cache.TryGetValue(cacheKey, out CacheEntry? cached) && cached is not null)
            {
                if (!sourceId.HasValue || sourceId.Value <= 0 ||
                    string.Equals(cached.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    return cached.Categories;
                }
            }

            var cats = await _torznab.FetchCapsAsync(torznabUrl, authMode, apiKey, ct);
            var raw = cats
                .Where(c => c.id > 0)
                .Select(c => new RawCategory(c.id, c.name))
                .ToList();

            if (raw.Count > 0)
                _cache.Set(cacheKey, new CacheEntry(fingerprint, raw), CacheTtl);

            return raw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Caps fetch failed for {Url}", torznabUrl);
            warnings.Add("Caps indisponible.");
            return new List<RawCategory>();
        }
    }

    private static string BuildFingerprint(string torznabUrl, string authMode, string apiKey)
    {
        var raw = $"{torznabUrl}|{authMode}|{apiKey}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string BuildCacheKey(long? sourceId, string fingerprint)
    {
        if (sourceId.HasValue && sourceId.Value > 0)
            return $"caps:source:{sourceId.Value}";
        return $"caps:torznab:{fingerprint}";
    }

    private sealed record CacheEntry(string Fingerprint, List<RawCategory> Categories);
    private sealed record RawCategory(int Id, string Name);
}
