using System;
using System.Collections.Generic;
using System.Linq;

namespace Feedarr.Api.Services.Categories;

public static class CategorySelection
{
    private static readonly string[] UnifiedPriority =
    {
        "games",
        "spectacle",
        "shows",
        "anime",
        "series",
        "films",
        "other"
    };

    private static readonly HashSet<string> ProtectedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "games",
        "spectacle",
        "shows",
        "anime"
    };

    public static int? PickBestCategoryId(IReadOnlyCollection<int> ids, Dictionary<int, (string key, string label)> map)
    {
        if (ids is null || ids.Count == 0 || map is null || map.Count == 0) return null;

        var candidates = ids.Where(id => map.ContainsKey(id)).ToList();
        if (candidates.Count == 0) return null;

        var protectedCandidates = candidates
            .Where(id => ProtectedKeys.Contains(map[id].key))
            .ToList();
        if (protectedCandidates.Count > 0)
            candidates = protectedCandidates;

        var specificCandidates = candidates.Where(id => id >= 10000).ToList();
        if (specificCandidates.Count > 0)
            candidates = specificCandidates;

        int? bestId = null;
        var bestRank = 999;
        foreach (var id in candidates)
        {
            var rank = GetUnifiedRank(map[id].key);
            if (rank < bestRank)
            {
                bestRank = rank;
                bestId = id;
            }
        }

        return bestId;
    }

    private static int GetUnifiedRank(string key)
    {
        var normalized = (key ?? "").Trim().ToLowerInvariant();
        var idx = Array.IndexOf(UnifiedPriority, normalized);
        return idx < 0 ? 999 : idx;
    }
}
