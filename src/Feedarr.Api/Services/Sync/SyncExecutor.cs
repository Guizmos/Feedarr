using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Torznab;

namespace Feedarr.Api.Services.Sync;

public sealed class SyncExecutor : ISyncExecutor
{
    private readonly TorznabClient _torznab;
    private readonly SourceRepository _sources;
    private readonly ReleaseRepository _releases;
    private readonly ActivityRepository _activity;
    private readonly IPosterFetchQueue _posterQueue;
    private readonly PosterFetchJobFactory _posterJobs;
    private readonly UnifiedCategoryResolver _resolver;
    private readonly RetentionService _retention;
    private readonly ProviderStatsService _providerStats;
    private readonly ILogger<SyncExecutor> _log;

    public SyncExecutor(
        TorznabClient torznab,
        SourceRepository sources,
        ReleaseRepository releases,
        ActivityRepository activity,
        IPosterFetchQueue posterQueue,
        PosterFetchJobFactory posterJobs,
        UnifiedCategoryResolver resolver,
        RetentionService retention,
        ProviderStatsService providerStats,
        ILogger<SyncExecutor> log)
    {
        _torznab = torznab;
        _sources = sources;
        _releases = releases;
        _activity = activity;
        _posterQueue = posterQueue;
        _posterJobs = posterJobs;
        _resolver = resolver;
        _retention = retention;
        _providerStats = providerStats;
        _log = log;
    }

    public async Task<SyncExecutionResult> ExecuteAsync(SyncPlan plan, CancellationToken ct)
    {
        var source = plan.Input.Source;
        var id = source.Id;
        var name = source.Name ?? "";
        var url = source.TorznabUrl ?? "";
        var apiKey = source.ApiKey ?? "";
        var authMode = source.AuthMode ?? "query";
        var prefix = plan.Telemetry.LogPrefix;

        try
        {
            LogCategorySelectionSnapshot(plan);

            var sw = Stopwatch.StartNew();
            _log.LogInformation(
                "{Prefix} RSS fetch start [{Name}] correlationId={CorrelationId} url={Url} limit={Limit}",
                prefix,
                name,
                plan.Telemetry.CorrelationId,
                SensitiveUrlSanitizer.Sanitize(url),
                plan.Fetch.PerCategoryLimit);

            var rssRes = await _torznab.FetchLatestAsync(
                url,
                authMode,
                apiKey,
                plan.Fetch.PerCategoryLimit,
                ct,
                allowSearch: plan.Fetch.AllowSearchInitial).ConfigureAwait(false);

            var rssItems = rssRes.items;
            var usedMode = rssRes.usedMode;

            _log.LogInformation(
                "{Prefix} RSS fetch done [{Name}] correlationId={CorrelationId} mode={Mode} itemsCount={Count} cats={Cats} unifiedKeys={UnifiedKeys}",
                prefix,
                name,
                plan.Telemetry.CorrelationId,
                usedMode,
                rssItems.Count,
                SyncPlanningHelpers.SummarizeCats(rssItems),
                SyncPlanningHelpers.SummarizeUnifiedKeys(rssItems, plan.Filter.CategoryMap, _resolver, name));

            var (missingCats, lowCats, targetPerCat) = SyncPlanningHelpers.ComputeFallbackCategories(
                rssItems,
                plan.Filter.SelectedCategoryIds,
                plan.Filter.CategoryMap,
                _resolver,
                name,
                plan.Fetch.PerCategoryLimit);

            var fallbackCandidates = new HashSet<int>(missingCats);
            foreach (var categoryId in lowCats)
                fallbackCandidates.Add(categoryId);

            _log.LogInformation(
                "{Prefix} Fallback check [{Name}] correlationId={CorrelationId} selectedCats={SelectedCats} missingCats={MissingCats} lowCats={LowCats} targetPerCat={Target} rssOnly={RssOnly}",
                prefix,
                name,
                plan.Telemetry.CorrelationId,
                plan.Filter.SelectedCategoryIds.Count > 0 ? string.Join(",", plan.Filter.SelectedCategoryIds.OrderBy(x => x)) : "-",
                missingCats.Count > 0 ? string.Join(",", missingCats.OrderBy(x => x)) : "-",
                lowCats.Count > 0 ? string.Join(",", lowCats.OrderBy(x => x)) : "-",
                targetPerCat,
                plan.Fetch.RssOnly);

            List<TorznabItem> items;
            string? fallbackMode = null;
            var usedAggregated = false;

            if (plan.Fetch.EnableCategoryFallback && !plan.Fetch.RssOnly && plan.Filter.SelectedCategoryIds.Count > 0)
            {
                if (rssItems.Count == 0 || fallbackCandidates.Count > 0)
                {
                    var fallbackCats = rssItems.Count == 0
                        ? plan.Filter.SelectedCategoryIds.ToList()
                        : fallbackCandidates.ToList();

                    if (fallbackCats.Count > 0)
                    {
                        _log.LogInformation(
                            "{Prefix} Torznab fallback start [{Name}] correlationId={CorrelationId} catIds={CatIds}",
                            prefix,
                            name,
                            plan.Telemetry.CorrelationId,
                            string.Join(",", fallbackCats.OrderBy(x => x)));

                        var fallback = await _torznab.FetchLatestByCategoriesAsync(
                            url,
                            authMode,
                            apiKey,
                            plan.Fetch.PerCategoryLimit,
                            fallbackCats,
                            ct).ConfigureAwait(false);

                        fallbackMode = fallback.usedMode;
                        usedAggregated = fallback.usedAggregated;
                        _log.LogInformation(
                            "{Prefix} Torznab fallback done [{Name}] correlationId={CorrelationId} itemsCount={Count} cats={Cats}",
                            prefix,
                            name,
                            plan.Telemetry.CorrelationId,
                            fallback.items.Count,
                            SyncPlanningHelpers.SummarizeCats(fallback.items));

                        items = SyncPlanningHelpers.MergePreferFirst(rssItems, fallback.items);
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

            if (plan.Telemetry.RecordIndexerQuery)
                _providerStats.RecordIndexerQuery(true);

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
                "{Prefix} RAW [{Name}] correlationId={CorrelationId} raw={RawCount} perCatLimit={PerCatLimit} globalLimit={GlobalLimit} mode={Mode} syncMode={SyncMode} fallbackMode={FallbackMode} aggregated={Aggregated}",
                prefix,
                name,
                plan.Telemetry.CorrelationId,
                rawCount,
                plan.Fetch.PerCategoryLimit,
                plan.Db.GlobalLimit,
                usedMode,
                syncMode,
                fallbackMode ?? "-",
                usedAggregated);

            var selectionFallbackUsed = CategorySelectionAudit.ShouldUseFallback(plan.Filter.SelectedCategoryIds);
            if (plan.Filter.SelectedCategoryIds.Count > 0)
            {
                var selectionResult = SyncPlanningHelpers.FilterBySelection(
                    items,
                    plan.Filter.SelectedCategoryIds,
                    plan.Filter.SelectedUnifiedKeys,
                    plan.Filter.CategoryMap,
                    _resolver,
                    name);

                items = selectionResult.Items;
                _log.LogInformation(
                    "{Prefix} CATEGORY FILTER [{Name}] correlationId={CorrelationId} selectedCats={SelectedCats} fetchedCats={FetchedCats} keptCats={KeptCats} droppedItemsCount={Dropped}",
                    prefix,
                    name,
                    plan.Telemetry.CorrelationId,
                    string.Join(",", plan.Filter.SelectedCategoryIds.OrderBy(x => x)),
                    selectionResult.FetchedCategories.Count > 0 ? string.Join(",", selectionResult.FetchedCategories.OrderBy(x => x)) : "-",
                    selectionResult.KeptCategories.Count > 0 ? string.Join(",", selectionResult.KeptCategories.OrderBy(x => x)) : "-",
                    selectionResult.DroppedNotSelectedCount + selectionResult.DroppedMissingCategoryCount);
            }
            else
            {
                _log.LogInformation(
                    "{Prefix} CATEGORY FILTER [{Name}] correlationId={CorrelationId} no active category mappings; dropping all fetched items",
                    prefix,
                    name,
                    plan.Telemetry.CorrelationId);
                items = new List<TorznabItem>();
            }

            var countBeforeCategoryMapFilter = items.Count;
            var categoryMapResult = SyncPlanningHelpers.MapCategories(items, plan.Filter.SelectedCategoryIds, plan.Filter.CategoryMap);
            items = categoryMapResult.Items;

            if (plan.Telemetry.EmitCategoryDebugActivity && categoryMapResult.SeenCategories.Count > 0)
            {
                var top = string.Join(", ",
                    categoryMapResult.SeenCategories
                        .OrderByDescending(kvp => kvp.Value)
                        .Take(8)
                        .Select(kvp => $"{kvp.Key}={kvp.Value}"));

                var missingCatsSummary = plan.Filter.SelectedCategoryIds
                    .Where(categoryId => !categoryMapResult.SeenCategories.ContainsKey(categoryId))
                    .Take(8)
                    .ToList();

                var msg = missingCatsSummary.Count > 0
                    ? $"Sync debug: fetched={items.Count}, mode={syncMode}, cats={top}, missing={string.Join(",", missingCatsSummary)}"
                    : $"Sync debug: fetched={items.Count}, mode={syncMode}, cats={top}";

                _activity.Add(id, "info", "sync", msg);
            }

            var filteredCount = items.Count;
            var dropped = countBeforeCategoryMapFilter - filteredCount;
            _log.LogInformation(
                "{Prefix} AFTER CATEGORY FILTER [{Name}] correlationId={CorrelationId} raw={RawCount} filtered={FilteredCount} dropped={Dropped} missingCategory={MissingCategory} noMapMatchCount={NoMapMatchCount} fallbackSelectedCategoryCount={FallbackSelectedCategoryCount}",
                prefix,
                name,
                plan.Telemetry.CorrelationId,
                countBeforeCategoryMapFilter,
                filteredCount,
                dropped,
                categoryMapResult.MissingCategoryCount,
                categoryMapResult.NoMapMatchCount,
                categoryMapResult.FallbackSelectedCategoryCount);

            if (categoryMapResult.FallbackSelectedCategoryCount > 0 || categoryMapResult.NoMapMatchCount > 0)
            {
                _activity.Add(
                    id,
                    "info",
                    "sync",
                    $"Sync category-map: fallbackSelectedCategoryCount={categoryMapResult.FallbackSelectedCategoryCount}, noMapMatchCount={categoryMapResult.NoMapMatchCount}, fallbackSamples={(categoryMapResult.FallbackSamples.Count > 0 ? string.Join(" | ", categoryMapResult.FallbackSamples) : "-")}, droppedSamples={(categoryMapResult.NoMapSamples.Count > 0 ? string.Join(" | ", categoryMapResult.NoMapSamples) : "-")}");
            }

            if (_log.IsEnabled(LogLevel.Debug))
            {
                foreach (var item in items)
                {
                    var rawIds = string.Join(",", SyncPlanningHelpers.GetRawCategoryIds(item));
                    var unifiedKey = item.CategoryId.HasValue && plan.Filter.CategoryMap.TryGetValue(item.CategoryId.Value, out var entry)
                        ? entry.key
                        : "";
                    _log.LogDebug(
                        "{Prefix} item title={Title} rawCategoryIds={RawIds} categoryId={CategoryId} unifiedKey={UnifiedKey}",
                        prefix,
                        item.Title,
                        rawIds,
                        item.CategoryId,
                        unifiedKey);
                }
            }

            _log.LogInformation(
                "{Prefix} CATEGORY SELECTION FALLBACK [{Name}] correlationId={CorrelationId} sourceId={SourceId} usedFallback={UsedFallback} fallbackSelectedCategoryCount={FallbackSelectedCategoryCount} noMapMatchCount={NoMapMatchCount} reason={Reason}",
                prefix,
                name,
                plan.Telemetry.CorrelationId,
                id,
                selectionFallbackUsed,
                categoryMapResult.FallbackSelectedCategoryCount,
                categoryMapResult.NoMapMatchCount,
                plan.Filter.SelectionReason);

            var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var insertedNew = _releases.UpsertMany(id, name, items, nowTs, plan.Db.DefaultSeen, plan.Filter.CategoryMap);
            var (retentionResult, postersPurged, failedDeletes) = _retention.ApplyRetention(id, plan.Fetch.PerCategoryLimit, plan.Db.GlobalLimit);

            var (posterRequested, posterEnqueued, posterCoalesced, posterTimedOut) = await EnqueuePosterJobsAsync(plan, ct).ConfigureAwait(false);

            _log.LogInformation(
                "{Prefix} posterJobs [{Name}] correlationId={CorrelationId} requested={Requested} enqueued={Enqueued} coalesced={Coalesced} timedOut={TimedOut}",
                prefix,
                name,
                plan.Telemetry.CorrelationId,
                posterRequested,
                posterEnqueued,
                posterCoalesced,
                posterTimedOut);

            _log.LogInformation(
                "{Prefix} RETENTION [{Name}] correlationId={CorrelationId} insertedNew={InsertedNew} totalBeforeRetention={TotalBefore} perKeyBefore={PerKeyBefore} perKeyAfter={PerKeyAfter} purgedByPerCat={PurgedPerCat} purgedByGlobal={PurgedGlobal} postersPurged={PostersPurged}",
                prefix,
                name,
                plan.Telemetry.CorrelationId,
                insertedNew,
                retentionResult.TotalBefore,
                SyncPlanningHelpers.FormatKeyCounts(retentionResult.PerKeyBefore),
                SyncPlanningHelpers.FormatKeyCounts(retentionResult.PerKeyAfter),
                retentionResult.PurgedByPerCategory,
                retentionResult.PurgedByGlobal,
                postersPurged);

            _log.LogInformation(
                "{Prefix} UPSERTED [{Name}] correlationId={CorrelationId} insertedNew={InsertedNew} items={ItemsCount}",
                prefix,
                name,
                plan.Telemetry.CorrelationId,
                insertedNew,
                items.Count);

            _sources.UpdateLastSync(id, "ok", null);
            _activity.Add(
                id,
                "info",
                "sync",
                BuildSuccessActivityMessage(plan, name, items.Count, syncMode),
                dataJson: BuildSuccessActivityDataJson(
                    itemsCount: items.Count,
                    usedMode: usedMode,
                    syncMode: syncMode,
                    insertedNew: insertedNew,
                    totalBeforeRetention: retentionResult.TotalBefore,
                    purgedByPerCategory: retentionResult.PurgedByPerCategory,
                    purgedByGlobal: retentionResult.PurgedByGlobal,
                    postersPurged: postersPurged,
                    failedPosterDeletes: failedDeletes,
                    elapsedMs: elapsedMs,
                    correlationId: plan.Telemetry.CorrelationId,
                    seenCategories: categoryMapResult.SeenCategories,
                    categoryMap: plan.Filter.CategoryMap));

            return new SyncExecutionResult(
                Ok: true,
                SourceId: id,
                SourceName: name,
                CorrelationId: plan.Telemetry.CorrelationId,
                UsedMode: usedMode,
                SyncMode: syncMode,
                ItemsCount: items.Count,
                InsertedNew: insertedNew,
                Error: null,
                ElapsedMs: elapsedMs,
                PosterRequested: posterRequested,
                PosterEnqueued: posterEnqueued,
                PosterCoalesced: posterCoalesced,
                PosterTimedOut: posterTimedOut);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (plan.Telemetry.RecordIndexerQuery)
                _providerStats.RecordIndexerQuery(false);

            var safeError = ErrorMessageSanitizer.ToOperationalMessage(ex, "sync failed");
            _sources.UpdateLastSync(id, "error", safeError);
            _activity.Add(id, "error", "sync", BuildErrorActivityMessage(plan, name, safeError));

            return new SyncExecutionResult(
                Ok: false,
                SourceId: id,
                SourceName: name,
                CorrelationId: plan.Telemetry.CorrelationId,
                UsedMode: null,
                SyncMode: null,
                ItemsCount: 0,
                InsertedNew: 0,
                Error: safeError,
                ElapsedMs: 0,
                PosterRequested: 0,
                PosterEnqueued: 0,
                PosterCoalesced: 0,
                PosterTimedOut: 0);
        }
    }

    private async Task<(int requested, int enqueued, int coalesced, int timedOut)> EnqueuePosterJobsAsync(SyncPlan plan, CancellationToken ct)
    {
        var requested = 0;
        var enqueued = 0;
        var coalesced = 0;
        var timedOut = 0;

        if (plan.Poster.SelectionMode == PosterSelectionMode.Seeds)
        {
            var seeds = _releases.GetNewPosterJobSeeds(plan.Input.Source.Id, plan.Poster.LastSyncAt);
            var jobs = new List<PosterFetchJob>();
            foreach (var seed in seeds)
            {
                var job = _posterJobs.CreateFromSeed(seed, plan.Poster.ForceRefresh);
                if (job is null) continue;

                requested++;
                jobs.Add(job);
            }

            var batch = await _posterQueue.EnqueueManyAsync(jobs, ct, PosterFetchQueue.DefaultBatchEnqueueTimeout).ConfigureAwait(false);
            enqueued += batch.Enqueued;
            coalesced += batch.Coalesced;
            timedOut += batch.TimedOut;
        }
        else
        {
            var ids = _releases.GetNewIdsWithoutPoster(plan.Input.Source.Id, plan.Poster.LastSyncAt);
            var jobs = new List<PosterFetchJob>();
            foreach (var id in ids)
            {
                var job = _posterJobs.Create(id, plan.Poster.ForceRefresh);
                if (job is null) continue;

                requested++;
                jobs.Add(job);
            }

            var batch = await _posterQueue.EnqueueManyAsync(jobs, ct, PosterFetchQueue.DefaultBatchEnqueueTimeout).ConfigureAwait(false);
            enqueued += batch.Enqueued;
            coalesced += batch.Coalesced;
            timedOut += batch.TimedOut;
        }

        if (timedOut > 0)
        {
            _log.LogWarning(
                "{Prefix} poster enqueue timed out sourceId={SourceId} sourceName={SourceName} requested={Requested} enqueued={Enqueued} coalesced={Coalesced} timedOut={TimedOut}; missing posters will be swept later",
                plan.Telemetry.LogPrefix,
                plan.Input.Source.Id,
                plan.Input.Source.Name ?? string.Empty,
                requested,
                enqueued,
                coalesced,
                timedOut);
        }

        return (requested, enqueued, coalesced, timedOut);
    }

    private void LogCategorySelectionSnapshot(SyncPlan plan)
    {
        var source = plan.Input.Source;
        var name = source.Name ?? "";
        var prefix = plan.Telemetry.LogPrefix;
        _log.LogInformation(
            "{Prefix} CATEGORY SELECTION LOAD [{Name}] correlationId={CorrelationId} sourceId={SourceId} source=source_category_mappings.cat_id persistedRaw={PersistedRaw} persistedCount={PersistedCount} mappedCount={MappedCount} unmappedCount={UnmappedCount} reason={Reason}",
            prefix,
            name,
            plan.Telemetry.CorrelationId,
            source.Id,
            CategorySelectionAudit.SummarizeIds(plan.Filter.PersistedCategoryIds, max: 60),
            plan.Filter.PersistedCategoryIds.Count,
            plan.Filter.MappedCategoryIds.Count,
            plan.Filter.UnmappedCategoryIds.Count,
            plan.Filter.SelectionReason);

        _log.LogInformation(
            "{Prefix} CATEGORY SELECTION EFFECTIVE [{Name}] correlationId={CorrelationId} sourceId={SourceId} normalizedSelection={NormalizedSelection} normalizedCount={NormalizedCount}",
            prefix,
            name,
            plan.Telemetry.CorrelationId,
            source.Id,
            CategorySelectionAudit.SummarizeIds(plan.Filter.SelectedCategoryIds, max: 60),
            plan.Filter.SelectedCategoryIds.Count);
    }

    private static string BuildSuccessActivityMessage(SyncPlan plan, string sourceName, int itemsCount, string syncMode)
    {
        return plan.Input.TriggerReason switch
        {
            "auto" => $"AutoSync OK [{sourceName}] ({itemsCount} items, mode={syncMode})",
            "scheduler" => $"Manual Run OK ({itemsCount} items, mode={syncMode})",
            _ => $"Sync OK ({itemsCount} items, mode={syncMode})"
        };
    }

    private static string BuildErrorActivityMessage(SyncPlan plan, string sourceName, string safeError)
    {
        return plan.Input.TriggerReason switch
        {
            "auto" => $"AutoSync ERROR [{sourceName}]: {safeError}",
            "scheduler" => $"Manual Run ERROR: {safeError}",
            _ => $"Sync ERROR: {safeError}"
        };
    }

    private static string BuildSuccessActivityDataJson(
        int itemsCount,
        string usedMode,
        string syncMode,
        int insertedNew,
        int totalBeforeRetention,
        int purgedByPerCategory,
        int purgedByGlobal,
        int postersPurged,
        int failedPosterDeletes,
        long elapsedMs,
        string correlationId,
        IReadOnlyDictionary<int, int> seenCategories,
        IReadOnlyDictionary<int, (string key, string label)> categoryMap)
    {
        var categoryIds = seenCategories.Keys.OrderBy(id => id).ToArray();
        var categories = BuildActivityCategoriesSnapshot(seenCategories, categoryMap);

        return JsonSerializer.Serialize(new
        {
            itemsCount,
            usedMode,
            syncMode,
            insertedNew,
            totalBeforeRetention,
            purgedByPerCat = purgedByPerCategory,
            purgedByGlobal,
            postersPurged,
            failedPosterDeletes,
            elapsedMs,
            correlationId,
            categoryIds,
            categories
        });
    }

    private static IReadOnlyList<ActivityCategorySnapshot> BuildActivityCategoriesSnapshot(
        IReadOnlyDictionary<int, int> seenCategories,
        IReadOnlyDictionary<int, (string key, string label)> categoryMap)
    {
        return seenCategories
            .OrderByDescending(kvp => kvp.Value)
            .ThenBy(kvp => kvp.Key)
            .Select(kvp =>
            {
                var id = kvp.Key;
                var count = kvp.Value;
                if (categoryMap.TryGetValue(id, out var mapped))
                {
                    return new ActivityCategorySnapshot(id, count, mapped.key, mapped.label);
                }

                return new ActivityCategorySnapshot(id, count, null, null);
            })
            .ToArray();
    }

    private sealed record ActivityCategorySnapshot(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("key")] string? Key,
        [property: JsonPropertyName("label")] string? Label);
}
