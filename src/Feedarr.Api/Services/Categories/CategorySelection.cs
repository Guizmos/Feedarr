using System;
using System.Collections.Generic;
using System.Linq;

namespace Feedarr.Api.Services.Categories;

public static class CategorySelection
{
    // Deterministic pick for mapped IDs:
    // - prefer mapped specific IDs over standard parents
    // - among candidates, prefer non-standard (>=10000), then standard leaves, then parents
    // - tie-breaker is highest cat ID (stable ordering)
    public static int? PickBestCategoryId(IReadOnlyCollection<int> ids, Dictionary<int, (string key, string label)> map)
    {
        if (ids is null || ids.Count == 0 || map is null || map.Count == 0) return null;

        var candidates = ids.Where(id => map.ContainsKey(id)).ToList();
        if (candidates.Count == 0) return null;

        return candidates
            .OrderByDescending(GetSpecificityScore)
            .ThenByDescending(id => id)
            .FirstOrDefault();
    }

    public static int? PickSelectedFallbackCategoryId(
        IReadOnlyCollection<int> ids,
        IReadOnlyCollection<int> selectedCategoryIds)
    {
        if (ids is null || ids.Count == 0 || selectedCategoryIds is null || selectedCategoryIds.Count == 0)
            return null;

        var selectedSet = new HashSet<int>(selectedCategoryIds);
        foreach (var id in ids)
        {
            if (id > 0 && selectedSet.Contains(id))
                return id;
        }

        return null;
    }

    private static int GetSpecificityScore(int catId)
    {
        if (catId >= 10000) return 3;
        if (catId is >= 1000 and <= 8999 && (catId % 1000) != 0) return 2;
        return 1;
    }
}
