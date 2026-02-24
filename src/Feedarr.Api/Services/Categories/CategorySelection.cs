using System;
using System.Collections.Generic;
using System.Linq;

namespace Feedarr.Api.Services.Categories;

public static class CategorySelection
{
    public static bool IsStandardById(int id) => id is >= 1000 and <= 8999;

    public static bool IsStandardParentId(int id) => IsStandardById(id) && (id % 1000) == 0;

    public static bool IsStandardLeafId(int id) => IsStandardById(id) && (id % 1000) != 0;

    public static int ToStandardParentId(int id)
        => IsStandardById(id) ? (id / 1000) * 1000 : id;

    public static HashSet<int> NormalizeSelectedCategoryIds(IEnumerable<int>? selectedIds)
    {
        var normalized = new HashSet<int>(
            (selectedIds ?? Enumerable.Empty<int>()).Where(id => id > 0));

        if (normalized.Count == 0)
            return normalized;

        var parentsWithSelectedLeaf = normalized
            .Where(IsStandardLeafId)
            .Select(ToStandardParentId)
            .ToHashSet();

        if (parentsWithSelectedLeaf.Count == 0)
            return normalized;

        normalized.RemoveWhere(id => IsStandardParentId(id) && parentsWithSelectedLeaf.Contains(id));
        return normalized;
    }

    public static HashSet<int> ExpandCategoryIdsForMatching(IEnumerable<int>? ids)
    {
        var expanded = new HashSet<int>();
        foreach (var id in ids ?? Enumerable.Empty<int>())
        {
            if (id <= 0) continue;
            expanded.Add(id);

            if (IsStandardLeafId(id))
                expanded.Add(ToStandardParentId(id));
        }

        return expanded;
    }

    public static bool MatchesSelectedCategoryIds(
        IEnumerable<int>? itemCategoryIds,
        IEnumerable<int>? selectedCategoryIds)
    {
        var normalizedSelected = NormalizeSelectedCategoryIds(selectedCategoryIds);
        if (normalizedSelected.Count == 0)
            return false;

        var itemIds = ExpandCategoryIdsForMatching(itemCategoryIds);
        if (itemIds.Count == 0)
            return false;

        return itemIds.Any(normalizedSelected.Contains);
    }

    // Deterministic pick for mapped IDs:
    // - prefer mapped specific IDs over standard parents
    // - among candidates, prefer non-standard (>=10000), then standard leaves, then parents
    // - tie-breaker is highest cat ID (stable ordering)
    public static int? PickBestCategoryId(IReadOnlyCollection<int> ids, Dictionary<int, (string key, string label)> map)
    {
        if (ids is null || ids.Count == 0 || map is null || map.Count == 0) return null;

        var candidates = ExpandCategoryIdsForMatching(ids).Where(id => map.ContainsKey(id)).ToList();
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

        var selectedSet = NormalizeSelectedCategoryIds(selectedCategoryIds);
        var candidates = ExpandCategoryIdsForMatching(ids).Where(selectedSet.Contains).ToList();
        if (candidates.Count == 0)
            return null;

        return candidates
            .OrderByDescending(GetSpecificityScore)
            .ThenByDescending(id => id)
            .First();
    }

    private static int GetSpecificityScore(int catId)
    {
        if (catId >= 10000) return 3;
        if (catId is >= 1000 and <= 8999 && (catId % 1000) != 0) return 2;
        return 1;
    }
}
