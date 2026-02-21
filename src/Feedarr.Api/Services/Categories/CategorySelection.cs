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

    // Golden rule for category handling:
    // 1) Torznab/Indexer category IDs are the upstream source of truth.
    // 2) source_categories is a local cache/lookup and must never be blocking.
    // 3) UnifiedCategory is the internal domain truth used by Feedarr.
    // Therefore: we never drop an item solely because level-2 cache mapping is incomplete.
    // If a selected category is present in the item, it can be used as a fallback.
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

    private static int GetUnifiedRank(string key)
    {
        var normalized = (key ?? "").Trim().ToLowerInvariant();
        var idx = Array.IndexOf(UnifiedPriority, normalized);
        return idx < 0 ? 999 : idx;
    }
}
