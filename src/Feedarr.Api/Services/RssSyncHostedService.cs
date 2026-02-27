using Feedarr.Api.Data.Repositories;
using System.Diagnostics;
using Feedarr.Api.Models;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Torznab;
using Feedarr.Api.Services.Backup;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Services;

public sealed class RssSyncHostedService : BackgroundService
{
    private static readonly HashSet<string> SensitiveQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "apikey",
        "api_key",
        "token",
        "access_token",
        "refresh_token",
        "key",
        "password",
        "pass",
        "secret",
        "client_secret",
        "authorization",
        "x-api-key"
    };

    private readonly ILogger<RssSyncHostedService> _log;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AppOptions _opts;
    private readonly BackupExecutionCoordinator _backupCoordinator;

    public RssSyncHostedService(
        ILogger<RssSyncHostedService> log,
        IServiceScopeFactory scopeFactory,
        IOptions<AppOptions> opts,
        BackupExecutionCoordinator backupCoordinator)
    {
        _log = log;
        _scopeFactory = scopeFactory;
        _opts = opts.Value;
        _backupCoordinator = backupCoordinator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // petite pause au démarrage (évite de spam dès le boot)
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;

            try
            {
                if (IsAutoSyncEnabled())
                {
                    using var lease = _backupCoordinator.TryEnterSyncActivity("rss-auto-sync");
                    if (lease is null)
                    {
                        _log.LogInformation("AutoSync skipped: backup operation in progress");
                    }
                    else
                    {
                        await RunOnce(stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // normal
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "RssSyncHostedService loop error");
            }

            // ✅ interval dynamique via DB (fallback appsettings)
            var intervalMin = GetIntervalMinutesFromDbOrOpts();
            if (!IsAutoSyncEnabled())
                intervalMin = Math.Max(intervalMin, 60);

            var elapsed = DateTimeOffset.UtcNow - started;
            var sleep = TimeSpan.FromMinutes(intervalMin) - elapsed;
            if (sleep < TimeSpan.FromSeconds(1)) sleep = TimeSpan.FromSeconds(1);

            await Task.Delay(sleep, stoppingToken);
        }
    }

    private int GetIntervalMinutesFromDbOrOpts()
    {
        var fallback = Math.Clamp(_opts.SyncIntervalMinutes, 1, 1440);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var settingsRepo = scope.ServiceProvider.GetRequiredService<SettingsRepository>();

            var general = settingsRepo.GetGeneral(new GeneralSettings
            {
                SyncIntervalMinutes = fallback,
                RssLimit = Math.Clamp(_opts.RssLimit <= 0 ? 100 : _opts.RssLimit, 1, 200)
            });

            return Math.Clamp(general.SyncIntervalMinutes, 1, 1440);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read sync interval from DB, using fallback {Fallback}min", fallback);
            return fallback;
        }
    }

    private bool IsAutoSyncEnabled()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var settingsRepo = scope.ServiceProvider.GetRequiredService<SettingsRepository>();
            var general = settingsRepo.GetGeneral(new GeneralSettings
            {
                SyncIntervalMinutes = Math.Clamp(_opts.SyncIntervalMinutes, 1, 1440),
                RssLimit = Math.Clamp(_opts.RssLimit <= 0 ? 100 : _opts.RssLimit, 1, 200),
                AutoSyncEnabled = true
            });
            return general.AutoSyncEnabled;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read auto-sync setting from DB, defaulting to enabled");
            return true;
        }
    }

    private async Task RunOnce(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();

        var sources = scope.ServiceProvider.GetRequiredService<SourceRepository>();
        var releases = scope.ServiceProvider.GetRequiredService<ReleaseRepository>();
        var activity = scope.ServiceProvider.GetRequiredService<ActivityRepository>();
        var torznab = scope.ServiceProvider.GetRequiredService<TorznabClient>();
        var resolver = scope.ServiceProvider.GetRequiredService<UnifiedCategoryResolver>();
        var posterQueue = scope.ServiceProvider.GetRequiredService<IPosterFetchQueue>();
        var posterJobs = scope.ServiceProvider.GetRequiredService<PosterFetchJobFactory>();
        var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();
        var providerStats = scope.ServiceProvider.GetRequiredService<ProviderStatsService>();

        // Record sync job start
        providerStats.RecordSyncJob(true);

        // ✅ settings dynamiques (DB) pour les limites RSS
        var perCatLimit = _opts.RssLimitPerCategory > 0 ? _opts.RssLimitPerCategory : _opts.RssLimit;
        if (perCatLimit <= 0) perCatLimit = 50;
        perCatLimit = Math.Clamp(perCatLimit, 1, 200);
        var globalLimit = _opts.RssLimitGlobalPerSource > 0 ? _opts.RssLimitGlobalPerSource : 250;
        globalLimit = Math.Clamp(globalLimit, 1, 2000);
        var defaultSeen = 0;
        try
        {
            var settingsRepo = scope.ServiceProvider.GetRequiredService<SettingsRepository>();
            var general = settingsRepo.GetGeneral(new GeneralSettings
            {
                SyncIntervalMinutes = Math.Clamp(_opts.SyncIntervalMinutes, 1, 1440),
                RssLimitPerCategory = perCatLimit,
                RssLimitGlobalPerSource = globalLimit,
                RssLimit = perCatLimit
            });

            perCatLimit = Math.Clamp(general.RssLimitPerCategory, 1, 200);
            globalLimit = Math.Clamp(general.RssLimitGlobalPerSource, 1, 2000);

            // Si HideSeenByDefault est activé, les nouvelles releases sont marquées comme vues par défaut
            var uiSettings = settingsRepo.GetUi(new UiSettings());
            if (uiSettings.HideSeenByDefault)
            {
                defaultSeen = 1;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read settings from DB for sync limits, using fallback values");
        }

        var list = sources.ListEnabledForSync();

        if (list.Count == 0)
        {
            _log.LogInformation("AutoSync: no sources");
            return;
        }

        foreach (var src in list)
        {
            ct.ThrowIfCancellationRequested();

            long id = src.Id;
            string name = src.Name ?? "";

            string url = src.TorznabUrl ?? "";
            string mode = src.AuthMode ?? "query";
            string apiKey = src.ApiKey ?? "";

            try
            {
                var categoryMap = sources.GetCategoryMappingMap(id);
                var syncCorrelationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
                var persistedCategoryIds = sources.GetActiveCategoryIds(id)
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
                    "AutoSync CATEGORY SELECTION LOAD [{Name}] correlationId={CorrelationId} sourceId={SourceId} source=source_category_mappings.cat_id persistedRaw={PersistedRaw} persistedCount={PersistedCount} mappedCount={MappedCount} unmappedCount={UnmappedCount} reason={Reason}",
                    name,
                    syncCorrelationId,
                    id,
                    CategorySelectionAudit.SummarizeIds(persistedCategoryIds, max: 60),
                    persistedCategoryIds.Count,
                    mappedCategoryIds.Count,
                    unmappedCategoryIds.Count,
                    selectionReason);
                _log.LogInformation(
                    "AutoSync CATEGORY SELECTION EFFECTIVE [{Name}] correlationId={CorrelationId} sourceId={SourceId} normalizedSelection={NormalizedSelection} normalizedCount={NormalizedCount}",
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
                bool usedAggregated = false;
                var rssOnly = _opts.RssOnlySync;
                _log.LogInformation("RSS fetch start [{Name}] url={Url} limit={Limit}", name, SanitizeUrl(url), perCatLimit);
                var rssRes = await torznab.FetchLatestAsync(url, mode, apiKey, perCatLimit, ct, allowSearch: false);
                var rssItems = rssRes.items;
                usedMode = rssRes.usedMode;
                _log.LogInformation(
                    "RSS fetch done [{Name}] mode={Mode} itemsCount={Count} cats={Cats} unifiedKeys={UnifiedKeys}",
                    name, usedMode, rssItems.Count, SummarizeCats(rssItems), SummarizeUnifiedKeys(rssItems, categoryMap, resolver, name));

                var (missingCats, lowCats, targetPerCat) = ComputeFallbackCategories(
                    rssItems, selectedCategoryIds, categoryMap, resolver, name, perCatLimit);
                var fallbackCandidates = new HashSet<int>(missingCats);
                foreach (var cid in lowCats) fallbackCandidates.Add(cid);
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
                            var fallback = await torznab.FetchLatestByCategoriesAsync(url, mode, apiKey, perCatLimit, fallbackCats, ct);
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

                // Record successful indexer query
                providerStats.RecordIndexerQuery(true);

                sources.SaveRssMode(id, usedMode);

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
                    "AutoSync RAW [{Name}] raw={RawCount} perCatLimit={PerCatLimit} globalLimit={GlobalLimit} mode={Mode} syncMode={SyncMode} fallbackMode={FallbackMode} aggregated={Aggregated}",
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

                    foreach (var it in items)
                    {
                        var ids = GetRawCategoryIds(it);
                        foreach (var cid in ids)
                        {
                            if (cid > 0) fetchedCats.Add(cid);
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
                            var unifiedKey = ResolveUnifiedKeyForSelection(name, ids, categoryMap, resolver);
                            matchesUnified = !string.IsNullOrWhiteSpace(unifiedKey) && selectedUnifiedKeys.Contains(unifiedKey);
                        }
                        if (!intersects && !matchesUnified)
                        {
                            droppedNotSelected++;
                            continue;
                        }

                        foreach (var cid in ids)
                        {
                            if (cid > 0)
                                keptCats.Add(cid);
                        }

                        kept.Add(it);
                    }

                    items = kept;
                    _log.LogInformation(
                        "AutoSync CATEGORY FILTER [{Name}] selectedCats={SelectedCats} fetchedCats={FetchedCats} keptCats={KeptCats} droppedItemsCount={Dropped}",
                        name,
                        string.Join(",", selectedSet.OrderBy(x => x)),
                        fetchedCats.Count > 0 ? string.Join(",", fetchedCats.OrderBy(x => x)) : "-",
                        keptCats.Count > 0 ? string.Join(",", keptCats.OrderBy(x => x)) : "-",
                        droppedNotSelected + droppedMissingCategory);
                }
                else
                {
                    _log.LogInformation("AutoSync CATEGORY FILTER [{Name}] no active category mappings; dropping all fetched items", name);
                    items = new List<TorznabItem>();
                }

                var countBeforeCategoryMapFilter = items.Count;
                var noMapMatchCount = 0;
                var fallbackSelectedCategoryCount = 0;
                if (categoryMap.Count > 0)
                {
                    var filtered = new List<TorznabItem>();
                    var missingCategory = 0;
                    var fallbackSamples = new List<string>();
                    var noMapSamples = new List<string>();
                    foreach (var it in items)
                    {
                        var ids = GetRawCategoryIds(it);

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
                                        $"title={BuildCategoryLogTitle(it.Title)}, ids={string.Join("/", ids)}, selected={string.Join("/", intersections)}, picked={picked.Value}");
                                }
                            }
                            else
                            {
                                noMapMatchCount++;
                                if (noMapSamples.Count < 5)
                                    noMapSamples.Add($"title={BuildCategoryLogTitle(it.Title)}, ids={string.Join("/", ids)}");
                                continue;
                            }
                        }
                        it.CategoryId = picked.Value;
                        filtered.Add(it);
                    }
                    items = filtered;

                    var filteredCount = items.Count;
                    var dropped = countBeforeCategoryMapFilter - filteredCount;
                    _log.LogInformation(
                        "AutoSync AFTER CATEGORY FILTER [{Name}] raw={RawCount} filtered={FilteredCount} dropped={Dropped} missingCategory={MissingCategory} noMapMatchCount={NoMapMatchCount} fallbackSelectedCategoryCount={FallbackSelectedCategoryCount}",
                        name, countBeforeCategoryMapFilter, filteredCount, dropped, missingCategory, noMapMatchCount, fallbackSelectedCategoryCount);

                    if (fallbackSelectedCategoryCount > 0 || noMapMatchCount > 0)
                    {
                        activity.Add(
                            id,
                            "info",
                            "sync",
                            $"AutoSync category-map: fallbackSelectedCategoryCount={fallbackSelectedCategoryCount}, noMapMatchCount={noMapMatchCount}, fallbackSamples={(fallbackSamples.Count > 0 ? string.Join(" | ", fallbackSamples) : "-")}, droppedSamples={(noMapSamples.Count > 0 ? string.Join(" | ", noMapSamples) : "-")}");
                    }

                    if (_log.IsEnabled(LogLevel.Debug))
                    {
                        foreach (var it in items)
                        {
                            var rawIds = string.Join(",", GetRawCategoryIds(it));
                            var unifiedKey = (it.CategoryId.HasValue && categoryMap.TryGetValue(it.CategoryId.Value, out var entry))
                                ? entry.key
                                : "";
                            _log.LogDebug(
                                "AutoSync item title={Title} rawCategoryIds={RawIds} categoryId={CategoryId} unifiedKey={UnifiedKey}",
                                it.Title, rawIds, it.CategoryId, unifiedKey);
                        }
                    }
                }

                _log.LogInformation(
                    "AutoSync CATEGORY SELECTION FALLBACK [{Name}] correlationId={CorrelationId} sourceId={SourceId} usedFallback={UsedFallback} fallbackSelectedCategoryCount={FallbackSelectedCategoryCount} noMapMatchCount={NoMapMatchCount} reason={Reason}",
                    name,
                    syncCorrelationId,
                    id,
                    selectionFallbackUsed,
                    fallbackSelectedCategoryCount,
                    noMapMatchCount,
                    selectionReason);

                var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var insertedNew = releases.UpsertMany(id, name, items, nowTs, defaultSeen, categoryMap);
                var (retentionResult, postersPurged) = retention.ApplyRetention(id, perCatLimit, globalLimit);

                var lastSyncAt = src.LastSyncAt ?? 0;
                var seeds = releases.GetNewPosterJobSeeds(id, lastSyncAt);
                foreach (var seed in seeds)
                {
                    var job = posterJobs.CreateFromSeed(seed, forceRefresh: false);
                    if (job is null) continue;
                    posterQueue.TryEnqueue(job);
                }

                _log.LogInformation(
                    "AutoSync RETENTION [{Name}] insertedNew={InsertedNew} totalBeforeRetention={TotalBefore} perKeyBefore={PerKeyBefore} perKeyAfter={PerKeyAfter} purgedByPerCat={PurgedPerCat} purgedByGlobal={PurgedGlobal} postersPurged={PostersPurged}",
                    name,
                    insertedNew,
                    retentionResult.TotalBefore,
                    FormatKeyCounts(retentionResult.PerKeyBefore),
                    FormatKeyCounts(retentionResult.PerKeyAfter),
                    retentionResult.PurgedByPerCategory,
                    retentionResult.PurgedByGlobal,
                    postersPurged);
                _log.LogInformation(
                    "AutoSync UPSERTED [{Name}] insertedNew={InsertedNew} items={ItemsCount}",
                    name, insertedNew, items.Count);
                sources.UpdateLastSync(id, "ok", null);

                activity.Add(id, "info", "sync",
                    $"AutoSync OK [{name}] ({items.Count} items, mode={syncMode})",
                    dataJson: $"{{\"itemsCount\":{items.Count},\"usedMode\":\"{usedMode}\",\"syncMode\":\"{syncMode}\",\"insertedNew\":{insertedNew},\"totalBeforeRetention\":{retentionResult.TotalBefore},\"purgedByPerCat\":{retentionResult.PurgedByPerCategory},\"purgedByGlobal\":{retentionResult.PurgedByGlobal},\"postersPurged\":{postersPurged},\"elapsedMs\":{elapsedMs}}}");
            }
            catch (Exception ex)
            {
                // Record failed indexer query
                providerStats.RecordIndexerQuery(false);
                var safeError = ErrorMessageSanitizer.ToOperationalMessage(ex, "auto sync failed");
                sources.UpdateLastSync(id, "error", safeError);
                activity.Add(id, "error", "sync", $"AutoSync ERROR [{name}]: {safeError}");
            }
        }
    }

    private static List<int> GetRawCategoryIds(TorznabItem it)
    {
        if (it.CategoryIds is { Count: > 0 })
            return it.CategoryIds;
        return it.CategoryId.HasValue ? new List<int> { it.CategoryId.Value } : new List<int>();
    }

    private static string BuildCategoryLogTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "-";

        var trimmed = title.Trim();
        return trimmed.Length <= 80 ? trimmed : trimmed[..80] + "...";
    }

    private static List<int> GetMissingCategoryIds(IEnumerable<TorznabItem> items, IReadOnlyCollection<int> requested)
    {
        if (requested.Count == 0) return new List<int>();
        var seen = new HashSet<int>();
        foreach (var it in items)
        {
            foreach (var id in GetRawCategoryIds(it))
            {
                if (id > 0) seen.Add(id);
            }
        }

        return requested.Where(id => !seen.Contains(id)).ToList();
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

        foreach (var it in items)
        {
            var ids = GetRawCategoryIds(it);
            foreach (var id in CategorySelection.ExpandCategoryIdsForMatching(ids))
            {
                if (id <= 0) continue;
                countsById[id] = countsById.TryGetValue(id, out var current) ? current + 1 : 1;
            }

            var key = ResolveUnifiedKeyForSelection(indexerName, ids, categoryMap, resolver);
            if (!string.IsNullOrWhiteSpace(key))
            {
                countsByKey[key] = countsByKey.TryGetValue(key, out var current) ? current + 1 : 1;
            }
        }

        var targetPerCat = Math.Max(1, limit / Math.Max(1, selectedIds.Count));
        var missing = new List<int>();
        var low = new List<int>();

        foreach (var id in selectedIds)
        {
            int count;
            if (categoryMap.TryGetValue(id, out var entry) && !string.IsNullOrWhiteSpace(entry.key))
            {
                countsByKey.TryGetValue(entry.key, out count);
            }
            else
            {
                countsById.TryGetValue(id, out count);
            }

            if (count == 0) missing.Add(id);
            else if (count < targetPerCat) low.Add(id);
        }

        return (missing, low, targetPerCat);
    }

    private static string GetMergeKey(TorznabItem it)
    {
        if (!string.IsNullOrWhiteSpace(it.Guid)) return it.Guid;
        if (!string.IsNullOrWhiteSpace(it.InfoHash)) return it.InfoHash;
        if (!string.IsNullOrWhiteSpace(it.DownloadUrl)) return it.DownloadUrl;
        if (!string.IsNullOrWhiteSpace(it.Link)) return it.Link;
        if (!string.IsNullOrWhiteSpace(it.Title)) return it.Title;
        return Guid.NewGuid().ToString("N");
    }

    private static List<TorznabItem> MergePreferFirst(IEnumerable<TorznabItem> primary, IEnumerable<TorznabItem> secondary)
    {
        var merged = new Dictionary<string, TorznabItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in primary)
        {
            merged[GetMergeKey(it)] = it;
        }
        foreach (var it in secondary)
        {
            var key = GetMergeKey(it);
            if (!merged.ContainsKey(key))
                merged[key] = it;
        }

        return merged.Values.ToList();
    }

    private static string SummarizeCats(IEnumerable<TorznabItem> items, int max = 8)
    {
        var counts = new Dictionary<int, int>();
        foreach (var it in items)
        {
            foreach (var cid in GetRawCategoryIds(it))
            {
                if (cid <= 0) continue;
                counts[cid] = counts.TryGetValue(cid, out var current) ? current + 1 : 1;
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
        foreach (var it in items)
        {
            var ids = GetRawCategoryIds(it);
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
            var key = Uri.UnescapeDataString(kv[0] ?? string.Empty);
            if (IsSensitiveQueryKey(key))
                continue;
            kept.Add(part);
        }

        var ub = new UriBuilder(uri) { Query = string.Join("&", kept) };
        return ub.Uri.ToString();
    }

    private static bool IsSensitiveQueryKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        if (SensitiveQueryKeys.Contains(key))
            return true;

        var compact = key
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return compact.Contains("token", StringComparison.Ordinal) ||
               compact.Contains("secret", StringComparison.Ordinal) ||
               compact.Contains("password", StringComparison.Ordinal) ||
               compact.Contains("auth", StringComparison.Ordinal) ||
               compact.EndsWith("key", StringComparison.Ordinal);
    }
}
