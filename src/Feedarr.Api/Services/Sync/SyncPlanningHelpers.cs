using Feedarr.Api.Models;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Torznab;

namespace Feedarr.Api.Services.Sync;

public sealed record SyncSelectionFilterResult(
    List<TorznabItem> Items,
    HashSet<int> FetchedCategories,
    HashSet<int> KeptCategories,
    int DroppedNotSelectedCount,
    int DroppedMissingCategoryCount);

public sealed record SyncCategoryMapResult(
    List<TorznabItem> Items,
    Dictionary<int, int> SeenCategories,
    int MissingCategoryCount,
    int NoMapMatchCount,
    int FallbackSelectedCategoryCount,
    List<string> FallbackSamples,
    List<string> NoMapSamples);

public static class SyncPlanningHelpers
{
    public static List<int> GetRawCategoryIds(TorznabItem item)
    {
        if (item.CategoryIds is { Count: > 0 })
            return item.CategoryIds;

        return item.CategoryId.HasValue
            ? new List<int> { item.CategoryId.Value }
            : new List<int>();
    }

    public static (List<int> missing, List<int> low, int targetPerCat) ComputeFallbackCategories(
        IEnumerable<TorznabItem> items,
        IReadOnlyCollection<int> selectedIds,
        Dictionary<int, (string key, string label)> categoryMap,
        UnifiedCategoryResolver resolver,
        string indexerName,
        int limit)
    {
        if (selectedIds.Count == 0) return (new List<int>(), new List<int>(), 0);

        var countsById = new Dictionary<int, int>();
        var countsByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var ids = GetRawCategoryIds(item);
            foreach (var id in CategorySelection.ExpandCategoryIdsForMatching(ids))
            {
                if (id <= 0) continue;
                countsById[id] = countsById.TryGetValue(id, out var current) ? current + 1 : 1;
            }

            var key = ResolveUnifiedKeyForSelection(indexerName, ids, categoryMap, resolver);
            if (!string.IsNullOrWhiteSpace(key))
                countsByKey[key] = countsByKey.TryGetValue(key, out var current) ? current + 1 : 1;
        }

        var targetPerCat = Math.Max(1, limit / Math.Max(1, selectedIds.Count));
        var missing = new List<int>();
        var low = new List<int>();

        foreach (var id in selectedIds)
        {
            int count;
            if (categoryMap.TryGetValue(id, out var entry) && !string.IsNullOrWhiteSpace(entry.key))
                countsByKey.TryGetValue(entry.key, out count);
            else
                countsById.TryGetValue(id, out count);

            if (count == 0) missing.Add(id);
            else if (count < targetPerCat) low.Add(id);
        }

        return (missing, low, targetPerCat);
    }

    public static List<TorznabItem> MergePreferFirst(IEnumerable<TorznabItem> primary, IEnumerable<TorznabItem> secondary)
    {
        var merged = new Dictionary<string, TorznabItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in primary)
            merged[GetMergeKey(item)] = item;

        foreach (var item in secondary)
        {
            var key = GetMergeKey(item);
            if (!merged.ContainsKey(key))
                merged[key] = item;
        }

        return merged.Values.ToList();
    }

    public static SyncSelectionFilterResult FilterBySelection(
        IEnumerable<TorznabItem> items,
        IReadOnlyCollection<int> selectedCategoryIds,
        IReadOnlyCollection<string> selectedUnifiedKeys,
        Dictionary<int, (string key, string label)> categoryMap,
        UnifiedCategoryResolver resolver,
        string indexerName)
    {
        var fetchedCats = new HashSet<int>();
        var keptCats = new HashSet<int>();
        var droppedNotSelected = 0;
        var droppedMissingCategory = 0;
        var kept = new List<TorznabItem>();

        foreach (var item in items)
        {
            var ids = GetRawCategoryIds(item);
            foreach (var categoryId in ids)
            {
                if (categoryId > 0)
                    fetchedCats.Add(categoryId);
            }

            if (ids.Count == 0)
            {
                droppedMissingCategory++;
                continue;
            }

            var intersects = CategorySelection.MatchesSelectedCategoryIds(ids, selectedCategoryIds);
            var matchesUnified = false;
            if (!intersects && selectedUnifiedKeys.Count > 0)
            {
                var unifiedKey = ResolveUnifiedKeyForSelection(indexerName, ids, categoryMap, resolver);
                matchesUnified = !string.IsNullOrWhiteSpace(unifiedKey) && selectedUnifiedKeys.Contains(unifiedKey);
            }

            if (!intersects && !matchesUnified)
            {
                droppedNotSelected++;
                continue;
            }

            foreach (var categoryId in ids)
            {
                if (categoryId > 0)
                    keptCats.Add(categoryId);
            }

            kept.Add(item);
        }

        return new SyncSelectionFilterResult(
            kept,
            fetchedCats,
            keptCats,
            droppedNotSelected,
            droppedMissingCategory);
    }

    public static Dictionary<int, int> BuildSeenCategoryCounts(IEnumerable<TorznabItem> items)
    {
        var seenCats = new Dictionary<int, int>();
        foreach (var item in items)
        {
            foreach (var categoryId in GetRawCategoryIds(item))
            {
                if (categoryId <= 0) continue;
                seenCats[categoryId] = seenCats.TryGetValue(categoryId, out var count) ? count + 1 : 1;
            }
        }

        return seenCats;
    }

    public static SyncCategoryMapResult MapCategories(
        IEnumerable<TorznabItem> items,
        IReadOnlyCollection<int> selectedCategoryIds,
        Dictionary<int, (string key, string label)> categoryMap)
    {
        var seenCats = BuildSeenCategoryCounts(items);
        var filtered = new List<TorznabItem>();
        var missingCategory = 0;
        var noMapMatchCount = 0;
        var fallbackSelectedCategoryCount = 0;
        var fallbackSamples = new List<string>();
        var noMapSamples = new List<string>();

        foreach (var item in items)
        {
            var ids = GetRawCategoryIds(item);
            if (ids.Count == 0)
            {
                missingCategory++;
                continue;
            }

            var picked = CategorySelection.PickBestCategoryId(ids, categoryMap);
            if (!picked.HasValue)
            {
                var fallbackPicked = CategorySelection.PickSelectedFallbackCategoryId(ids, selectedCategoryIds);
                if (fallbackPicked.HasValue)
                {
                    picked = fallbackPicked.Value;
                    fallbackSelectedCategoryCount++;
                    if (fallbackSamples.Count < 5)
                    {
                        var intersections = ids.Where(selectedCategoryIds.Contains).Distinct().ToList();
                        if (intersections.Count == 0)
                        {
                            intersections = CategorySelection.ExpandCategoryIdsForMatching(ids)
                                .Where(selectedCategoryIds.Contains)
                                .Distinct()
                                .ToList();
                        }

                        fallbackSamples.Add(
                            $"title={BuildCategoryLogTitle(item.Title)}, ids={string.Join("/", ids)}, selected={string.Join("/", intersections)}, picked={picked.Value}");
                    }
                }
                else
                {
                    noMapMatchCount++;
                    if (noMapSamples.Count < 5)
                        noMapSamples.Add($"title={BuildCategoryLogTitle(item.Title)}, ids={string.Join("/", ids)}");
                    continue;
                }
            }

            item.CategoryId = picked.Value;
            filtered.Add(item);
        }

        return new SyncCategoryMapResult(
            filtered,
            seenCats,
            missingCategory,
            noMapMatchCount,
            fallbackSelectedCategoryCount,
            fallbackSamples,
            noMapSamples);
    }

    public static string SummarizeCats(IEnumerable<TorznabItem> items, int max = 8)
    {
        var counts = BuildSeenCategoryCounts(items);
        if (counts.Count == 0) return "-";

        return string.Join(", ",
            counts.OrderByDescending(kvp => kvp.Value)
                .Take(max)
                .Select(kvp => $"{kvp.Key}={kvp.Value}"));
    }

    public static string SummarizeUnifiedKeys(
        IEnumerable<TorznabItem> items,
        Dictionary<int, (string key, string label)> categoryMap,
        UnifiedCategoryResolver resolver,
        string indexerName,
        int max = 6)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var ids = GetRawCategoryIds(item);
            var key = ResolveUnifiedKeyForSelection(indexerName, ids, categoryMap, resolver) ?? "other";
            counts[key] = counts.TryGetValue(key, out var current) ? current + 1 : 1;
        }

        if (counts.Count == 0) return "-";

        return string.Join(", ",
            counts.OrderByDescending(kvp => kvp.Value)
                .Take(max)
                .Select(kvp => $"{kvp.Key}={kvp.Value}"));
    }

    public static string FormatKeyCounts(Dictionary<string, int>? counts, int max = 8)
    {
        if (counts is null || counts.Count == 0) return "-";
        return string.Join(", ",
            counts.OrderByDescending(kvp => kvp.Value)
                .Take(max)
                .Select(kvp => $"{kvp.Key}={kvp.Value}"));
    }

    public static string? ResolveUnifiedKeyForSelection(
        string indexerName,
        IReadOnlyCollection<int> ids,
        Dictionary<int, (string key, string label)> categoryMap,
        UnifiedCategoryResolver resolver)
    {
        if (ids.Count == 0) return null;

        var bestId = CategorySelection.PickBestCategoryId(ids, categoryMap);
        if (bestId.HasValue && categoryMap.TryGetValue(bestId.Value, out var entry))
            return entry.key;

        var (stdId, specId) = UnifiedCategoryResolver.ResolveStdSpec(null, null, ids);
        var unified = resolver.Resolve(indexerName, stdId, specId, ids);
        if (unified == UnifiedCategory.Autre) return null;
        return UnifiedCategoryMappings.ToKey(unified);
    }

    public static string BuildCategoryLogTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "-";

        var trimmed = title.Trim();
        return trimmed.Length <= 80 ? trimmed : trimmed[..80] + "...";
    }

    private static string GetMergeKey(TorznabItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Guid)) return item.Guid;
        if (!string.IsNullOrWhiteSpace(item.InfoHash)) return item.InfoHash;
        if (!string.IsNullOrWhiteSpace(item.DownloadUrl)) return item.DownloadUrl;
        if (!string.IsNullOrWhiteSpace(item.Link)) return item.Link;
        if (!string.IsNullOrWhiteSpace(item.Title)) return item.Title;
        return Guid.NewGuid().ToString("N");
    }
}
