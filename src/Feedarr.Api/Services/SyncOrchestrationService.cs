using System.Diagnostics;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Torznab;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Services;

public sealed record SyncOrchestrationResult(
    bool Ok,
    string? UsedMode,
    string? SyncMode,
    int ItemsCount,
    int InsertedNew,
    string? Error);

public sealed class SyncOrchestrationService
{
    private readonly TorznabClient _torznab;
    private readonly SourceRepository _sources;
    private readonly ReleaseRepository _releases;
    private readonly ActivityRepository _activity;
    private readonly SettingsRepository _settings;
    private readonly IPosterFetchQueue _posterQueue;
    private readonly PosterFetchJobFactory _posterJobs;
    private readonly UnifiedCategoryResolver _resolver;
    private readonly RetentionService _retention;
    private readonly AppOptions _opts;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly ILogger<SyncOrchestrationService> _log;

    public SyncOrchestrationService(
        TorznabClient torznab,
        SourceRepository sources,
        ReleaseRepository releases,
        ActivityRepository activity,
        SettingsRepository settings,
        IPosterFetchQueue posterQueue,
        PosterFetchJobFactory posterJobs,
        UnifiedCategoryResolver resolver,
        RetentionService retention,
        IOptions<AppOptions> opts,
        IHostApplicationLifetime appLifetime,
        ILogger<SyncOrchestrationService> log)
    {
        _torznab = torznab;
        _sources = sources;
        _releases = releases;
        _activity = activity;
        _settings = settings;
        _posterQueue = posterQueue;
        _posterJobs = posterJobs;
        _resolver = resolver;
        _retention = retention;
        _opts = opts.Value;
        _appLifetime = appLifetime;
        _log = log;
    }

    public async Task<SyncOrchestrationResult> ExecuteManualSyncAsync(Source src, bool rssOnly, CancellationToken _)
    {
        var id = src.Id;
        var url = src.TorznabUrl;
        var key = src.ApiKey ?? "";
        var mode = src.AuthMode;
        var name = src.Name;

        using var syncCts = CancellationTokenSource.CreateLinkedTokenSource(_appLifetime.ApplicationStopping);
        syncCts.CancelAfter(TimeSpan.FromMinutes(5));
        var syncCt = syncCts.Token;

        try
        {
            var categoryMap = _sources.GetCategoryMappingMap(id);
            var perCatLimit = _opts.RssLimitPerCategory > 0 ? _opts.RssLimitPerCategory : _opts.RssLimit;
            if (perCatLimit <= 0) perCatLimit = 50;
            perCatLimit = Math.Clamp(perCatLimit, 1, 200);
            var globalLimit = _opts.RssLimitGlobalPerSource > 0 ? _opts.RssLimitGlobalPerSource : 250;
            globalLimit = Math.Clamp(globalLimit, 1, 2000);

            var defaultSeen = 0;
            try
            {
                var general = _settings.GetGeneral(new GeneralSettings
                {
                    SyncIntervalMinutes = Math.Clamp(_opts.SyncIntervalMinutes, 1, 1440),
                    RssLimitPerCategory = perCatLimit,
                    RssLimitGlobalPerSource = globalLimit,
                    RssLimit = perCatLimit,
                    AutoSyncEnabled = true
                });
                perCatLimit = Math.Clamp(general.RssLimitPerCategory, 1, 200);
                globalLimit = Math.Clamp(general.RssLimitGlobalPerSource, 1, 2000);

                var uiSettings = _settings.GetUi(new UiSettings());
                if (uiSettings.HideSeenByDefault)
                    defaultSeen = 1;
            }
            catch
            {
            }

            var syncCorrelationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
            var persistedCategoryIds = _sources.GetActiveCategoryIds(id)
                .Where(categoryId => categoryId > 0)
                .Distinct()
                .OrderBy(categoryId => categoryId)
                .ToList();
            var selectedCategoryIds = CategorySelection.NormalizeSelectedCategoryIds(persistedCategoryIds);
            var mappedCategoryIds = categoryMap.Keys
                .Where(categoryId => categoryId > 0)
                .Distinct()
                .OrderBy(categoryId => categoryId)
                .ToList();
            var unmappedCategoryIds = persistedCategoryIds
                .Where(categoryId => !categoryMap.ContainsKey(categoryId))
                .Distinct()
                .OrderBy(categoryId => categoryId)
                .ToList();
            var selectionReason = CategorySelectionAudit.InferReason(
                persistedWasNull: false,
                persistedCount: persistedCategoryIds.Count,
                parseErrorCount: 0,
                mappedCount: persistedCategoryIds.Count);
            _log.LogInformation(
                "ManualSync CATEGORY SELECTION LOAD [{Name}] correlationId={CorrelationId} sourceId={SourceId} source=source_category_mappings.cat_id persistedRaw={PersistedRaw} persistedCount={PersistedCount} mappedCount={MappedCount} unmappedCount={UnmappedCount} reason={Reason}",
                name,
                syncCorrelationId,
                id,
                CategorySelectionAudit.SummarizeIds(persistedCategoryIds, max: 60),
                persistedCategoryIds.Count,
                mappedCategoryIds.Count,
                unmappedCategoryIds.Count,
                selectionReason);
            _log.LogInformation(
                "ManualSync CATEGORY SELECTION EFFECTIVE [{Name}] correlationId={CorrelationId} sourceId={SourceId} normalizedSelection={NormalizedSelection} normalizedCount={NormalizedCount}",
                name,
                syncCorrelationId,
                id,
                CategorySelectionAudit.SummarizeIds(selectedCategoryIds, max: 60),
                selectedCategoryIds.Count);

            var selectedUnifiedKeys = new HashSet<string>(
                categoryMap.Values.Select(v => v.key).Where(k => !string.IsNullOrWhiteSpace(k)),
                StringComparer.OrdinalIgnoreCase);

            var sw = Stopwatch.StartNew();
            List<TorznabItem> items;
            string usedMode;
            string? fallbackMode = null;
            var usedAggregated = false;

            _log.LogInformation("RSS fetch start [{Name}] url={Url} limit={Limit}", name, SanitizeUrl(url), perCatLimit);
            var rssRes = await _torznab.FetchLatestAsync(url, mode, key, perCatLimit, syncCt, allowSearch: false);
            var rssItems = rssRes.items;
            usedMode = rssRes.usedMode;
            _log.LogInformation(
                "RSS fetch done [{Name}] mode={Mode} itemsCount={Count} cats={Cats} unifiedKeys={UnifiedKeys}",
                name, usedMode, rssItems.Count, SummarizeCats(rssItems), SummarizeUnifiedKeys(rssItems, categoryMap, _resolver, name));

            var (missingCats, lowCats, targetPerCat) = ComputeFallbackCategories(
                rssItems, selectedCategoryIds, categoryMap, _resolver, name, perCatLimit);
            var fallbackCandidates = new HashSet<int>(missingCats);
            foreach (var categoryId in lowCats) fallbackCandidates.Add(categoryId);

            _log.LogInformation(
                "Fallback check [{Name}] selectedCats={SelectedCats} missingCats={MissingCats} lowCats={LowCats} targetPerCat={Target} rssOnly={RssOnly}",
                name,
                selectedCategoryIds.Count > 0 ? string.Join(",", selectedCategoryIds.OrderBy(x => x)) : "-",
                missingCats.Count > 0 ? string.Join(",", missingCats.OrderBy(x => x)) : "-",
                lowCats.Count > 0 ? string.Join(",", lowCats.OrderBy(x => x)) : "-",
                targetPerCat,
                rssOnly);

            if (!rssOnly && selectedCategoryIds.Count > 0)
            {
                if (rssItems.Count == 0 || fallbackCandidates.Count > 0)
                {
                    var fallbackCats = rssItems.Count == 0 ? selectedCategoryIds.ToList() : fallbackCandidates.ToList();
                    if (fallbackCats.Count > 0)
                    {
                        _log.LogInformation(
                            "Torznab fallback start [{Name}] catIds={CatIds}",
                            name,
                            string.Join(",", fallbackCats.OrderBy(x => x)));
                        var fallback = await _torznab.FetchLatestByCategoriesAsync(url, mode, key, perCatLimit, fallbackCats, syncCt);
                        fallbackMode = fallback.usedMode;
                        usedAggregated = fallback.usedAggregated;
                        _log.LogInformation(
                            "Torznab fallback done [{Name}] itemsCount={Count} cats={Cats}",
                            name, fallback.items.Count, SummarizeCats(fallback.items));
                        items = MergePreferFirst(rssItems, fallback.items);
                    }
                    else
                    {
                        items = rssItems;
                    }
                }
                else
                {
                    items = rssItems;
                }
            }
            else
            {
                items = rssItems;
            }

            sw.Stop();
            var elapsedMs = sw.ElapsedMilliseconds;
            _sources.SaveRssMode(id, usedMode);

            var rawCount = items.Count;
            var rssUsed = rssItems.Count > 0 &&
                          (string.Equals(usedMode, "rss", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(usedMode, "recent", StringComparison.OrdinalIgnoreCase));
            var syncMode = rssUsed
                ? (!string.IsNullOrWhiteSpace(fallbackMode) ? "rss_plus_fallback" : "rss_only")
                : string.Equals(usedMode, "search", StringComparison.OrdinalIgnoreCase)
                    ? "torznab_only"
                    : "rss_only";

            _log.LogInformation(
                "ManualSync RAW [{Name}] raw={RawCount} perCatLimit={PerCatLimit} globalLimit={GlobalLimit} mode={Mode} syncMode={SyncMode} fallbackMode={FallbackMode} aggregated={Aggregated}",
                name, rawCount, perCatLimit, globalLimit, usedMode, syncMode, fallbackMode ?? "-", usedAggregated);

            var selectionFallbackUsed = CategorySelectionAudit.ShouldUseFallback(selectedCategoryIds);
            if (selectedCategoryIds.Count > 0)
            {
                var selectedSet = selectedCategoryIds;
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
                        if (categoryId > 0) fetchedCats.Add(categoryId);
                    }

                    if (ids.Count == 0)
                    {
                        droppedMissingCategory++;
                        continue;
                    }

                    var intersects = CategorySelection.MatchesSelectedCategoryIds(ids, selectedSet);

                    var matchesUnified = false;
                    if (!intersects && selectedUnifiedKeys.Count > 0)
                    {
                        var unifiedKey = ResolveUnifiedKeyForSelection(name, ids, categoryMap, _resolver);
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

                items = kept;
                _log.LogInformation(
                    "ManualSync CATEGORY FILTER [{Name}] selectedCats={SelectedCats} fetchedCats={FetchedCats} keptCats={KeptCats} droppedItemsCount={Dropped}",
                    name,
                    string.Join(",", selectedSet.OrderBy(x => x)),
                    fetchedCats.Count > 0 ? string.Join(",", fetchedCats.OrderBy(x => x)) : "-",
                    keptCats.Count > 0 ? string.Join(",", keptCats.OrderBy(x => x)) : "-",
                    droppedNotSelected + droppedMissingCategory);
            }
            else
            {
                _log.LogInformation("ManualSync CATEGORY FILTER [{Name}] no active category mappings; dropping all fetched items", name);
                items = new List<TorznabItem>();
            }

            var countBeforeCategoryMapFilter = items.Count;
            var noMapMatchCount = 0;
            var fallbackSelectedCategoryCount = 0;
            if (categoryMap.Count > 0)
            {
                var seenCats = new Dictionary<int, int>();
                foreach (var item in items)
                {
                    var ids = item.CategoryIds is { Count: > 0 }
                        ? item.CategoryIds
                        : (item.CategoryId.HasValue ? new List<int> { item.CategoryId.Value } : new List<int>());

                    foreach (var categoryId in ids)
                    {
                        if (categoryId <= 0) continue;
                        seenCats[categoryId] = seenCats.TryGetValue(categoryId, out var count) ? count + 1 : 1;
                    }
                }

                if (seenCats.Count > 0)
                {
                    var top = string.Join(", ",
                        seenCats.OrderByDescending(kvp => kvp.Value)
                            .Take(8)
                            .Select(kvp => $"{kvp.Key}={kvp.Value}"));

                    var missingCatsSummary = selectedCategoryIds
                        .Where(categoryId => !seenCats.ContainsKey(categoryId))
                        .Take(8)
                        .ToList();

                    var msg = missingCatsSummary.Count > 0
                        ? $"Sync debug: fetched={items.Count}, mode={syncMode}, cats={top}, missing={string.Join(",", missingCatsSummary)}"
                        : $"Sync debug: fetched={items.Count}, mode={syncMode}, cats={top}";

                    _activity.Add(id, "info", "sync", msg);
                }

                var filtered = new List<TorznabItem>();
                var missingCategory = 0;
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

                items = filtered;

                var filteredCount = items.Count;
                var dropped = countBeforeCategoryMapFilter - filteredCount;
                _log.LogInformation(
                    "ManualSync AFTER CATEGORY FILTER [{Name}] raw={RawCount} filtered={FilteredCount} dropped={Dropped} missingCategory={MissingCategory} noMapMatchCount={NoMapMatchCount} fallbackSelectedCategoryCount={FallbackSelectedCategoryCount}",
                    name, countBeforeCategoryMapFilter, filteredCount, dropped, missingCategory, noMapMatchCount, fallbackSelectedCategoryCount);

                if (fallbackSelectedCategoryCount > 0 || noMapMatchCount > 0)
                {
                    _activity.Add(
                        id,
                        "info",
                        "sync",
                        $"Sync category-map: fallbackSelectedCategoryCount={fallbackSelectedCategoryCount}, noMapMatchCount={noMapMatchCount}, fallbackSamples={(fallbackSamples.Count > 0 ? string.Join(" | ", fallbackSamples) : "-")}, droppedSamples={(noMapSamples.Count > 0 ? string.Join(" | ", noMapSamples) : "-")}");
                }

                if (_log.IsEnabled(LogLevel.Debug))
                {
                    foreach (var item in items)
                    {
                        var rawIds = string.Join(",", GetRawCategoryIds(item));
                        var unifiedKey = item.CategoryId.HasValue && categoryMap.TryGetValue(item.CategoryId.Value, out var entry)
                            ? entry.key
                            : "";
                        _log.LogDebug(
                            "ManualSync item title={Title} rawCategoryIds={RawIds} categoryId={CategoryId} unifiedKey={UnifiedKey}",
                            item.Title, rawIds, item.CategoryId, unifiedKey);
                    }
                }
            }

            _log.LogInformation(
                "ManualSync CATEGORY SELECTION FALLBACK [{Name}] correlationId={CorrelationId} sourceId={SourceId} usedFallback={UsedFallback} fallbackSelectedCategoryCount={FallbackSelectedCategoryCount} noMapMatchCount={NoMapMatchCount} reason={Reason}",
                name,
                syncCorrelationId,
                id,
                selectionFallbackUsed,
                fallbackSelectedCategoryCount,
                noMapMatchCount,
                selectionReason);

            var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var insertedNew = _releases.UpsertMany(id, name, items, nowTs, defaultSeen, categoryMap);
            var (retentionResult, postersPurged) = _retention.ApplyRetention(id, perCatLimit, globalLimit);

            var lastSyncAt = src.LastSyncAt ?? 0;
            var newIds = _releases.GetNewIdsWithoutPoster(id, lastSyncAt);
            foreach (var releaseId in newIds)
            {
                var job = _posterJobs.Create(releaseId, forceRefresh: false);
                if (job is null) continue;
                _posterQueue.TryEnqueue(job);
            }

            _sources.UpdateLastSync(id, "ok", null);

            _log.LogInformation(
                "ManualSync RETENTION [{Name}] insertedNew={InsertedNew} totalBeforeRetention={TotalBefore} perKeyBefore={PerKeyBefore} perKeyAfter={PerKeyAfter} purgedByPerCat={PurgedPerCat} purgedByGlobal={PurgedGlobal} postersPurged={PostersPurged}",
                name,
                insertedNew,
                retentionResult.TotalBefore,
                FormatKeyCounts(retentionResult.PerKeyBefore),
                FormatKeyCounts(retentionResult.PerKeyAfter),
                retentionResult.PurgedByPerCategory,
                retentionResult.PurgedByGlobal,
                postersPurged);
            _log.LogInformation(
                "ManualSync UPSERTED [{Name}] insertedNew={InsertedNew} items={ItemsCount}",
                name, insertedNew, items.Count);

            _activity.Add(id, "info", "sync", $"Sync OK ({items.Count} items, mode={syncMode})",
                dataJson: $"{{\"itemsCount\":{items.Count},\"usedMode\":\"{usedMode}\",\"syncMode\":\"{syncMode}\",\"insertedNew\":{insertedNew},\"totalBeforeRetention\":{retentionResult.TotalBefore},\"purgedByPerCat\":{retentionResult.PurgedByPerCategory},\"purgedByGlobal\":{retentionResult.PurgedByGlobal},\"postersPurged\":{postersPurged},\"elapsedMs\":{elapsedMs}}}");

            return new SyncOrchestrationResult(true, usedMode, syncMode, items.Count, insertedNew, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Manual sync failed for sourceId={SourceId}", id);
            var safeError = ErrorMessageSanitizer.ToOperationalMessage(ex, "sync failed");
            _sources.UpdateLastSync(id, "error", safeError);
            _activity.Add(id, "error", "sync", $"Sync ERROR: {safeError}");
            return new SyncOrchestrationResult(false, null, null, 0, 0, safeError);
        }
    }

    private static List<int> GetRawCategoryIds(TorznabItem item)
    {
        if (item.CategoryIds is { Count: > 0 })
            return item.CategoryIds;
        return item.CategoryId.HasValue ? new List<int> { item.CategoryId.Value } : new List<int>();
    }

    private static string BuildCategoryLogTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "-";

        var trimmed = title.Trim();
        return trimmed.Length <= 80 ? trimmed : trimmed[..80] + "...";
    }

    private static (List<int> missing, List<int> low, int targetPerCat) ComputeFallbackCategories(
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

    private static string GetMergeKey(TorznabItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Guid)) return item.Guid;
        if (!string.IsNullOrWhiteSpace(item.InfoHash)) return item.InfoHash;
        if (!string.IsNullOrWhiteSpace(item.DownloadUrl)) return item.DownloadUrl;
        if (!string.IsNullOrWhiteSpace(item.Link)) return item.Link;
        if (!string.IsNullOrWhiteSpace(item.Title)) return item.Title;
        return Guid.NewGuid().ToString("N");
    }

    private static List<TorznabItem> MergePreferFirst(IEnumerable<TorznabItem> primary, IEnumerable<TorznabItem> secondary)
    {
        var merged = new Dictionary<string, TorznabItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in primary)
        {
            merged[GetMergeKey(item)] = item;
        }

        foreach (var item in secondary)
        {
            var key = GetMergeKey(item);
            if (!merged.ContainsKey(key))
                merged[key] = item;
        }

        return merged.Values.ToList();
    }

    private static string SummarizeCats(IEnumerable<TorznabItem> items, int max = 8)
    {
        var counts = new Dictionary<int, int>();
        foreach (var item in items)
        {
            foreach (var categoryId in GetRawCategoryIds(item))
            {
                if (categoryId <= 0) continue;
                counts[categoryId] = counts.TryGetValue(categoryId, out var current) ? current + 1 : 1;
            }
        }

        if (counts.Count == 0) return "-";
        return string.Join(", ",
            counts.OrderByDescending(kvp => kvp.Value)
                .Take(max)
                .Select(kvp => $"{kvp.Key}={kvp.Value}"));
    }

    private static string SummarizeUnifiedKeys(
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

    private static string FormatKeyCounts(Dictionary<string, int>? counts, int max = 8)
    {
        if (counts is null || counts.Count == 0) return "-";
        return string.Join(", ",
            counts.OrderByDescending(kvp => kvp.Value)
                .Take(max)
                .Select(kvp => $"{kvp.Key}={kvp.Value}"));
    }

    private static string? ResolveUnifiedKeyForSelection(
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

    private static string SanitizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        var query = uri.Query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(query))
            return url;

        var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>();
        foreach (var part in parts)
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 0) continue;
            if (string.Equals(kv[0], "apikey", StringComparison.OrdinalIgnoreCase))
                continue;
            kept.Add(part);
        }

        var ub = new UriBuilder(uri) { Query = string.Join("&", kept) };
        return ub.Uri.ToString();
    }
}
