using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Sync;
using Feedarr.Api.Services.Torznab;

namespace Feedarr.Api.Services.DirectRss;

/// <summary>Fetch all active feeds, merge once, then use the existing ingestion and retention pipeline.</summary>
public sealed class DirectRssSyncService(DirectRssClient client, DirectRssRepository feeds,
    ActivityRepository activity)
{
    public async Task<DirectRssMergeResult> FetchAsync(SyncPlan plan, CancellationToken ct)
    {
        var sourceId = plan.Input.Source.Id;
        var entries = new List<DirectRssEntry>();
        var existing = feeds.ExistingCategories(sourceId);
        var failed = 0;
        var succeeded = 0;
        foreach (var feed in feeds.List(sourceId).Where(f => f.Enabled))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var fetched = await client.FetchAsync(feed.Url, feed.Id, feed.CategoryKey, ct);
                // A bounded RSS window with no overlap may have lost releases between polls.
                var saturated = feed.LastCheckedAt is not null && existing.Count > 0 && fetched.Count > 0 && !fetched.Any(e => existing.ContainsKey(e.Item.Guid));
                feeds.RecordFetch(feed.Id, fetched.Count, null, saturated);
                entries.AddRange(fetched);
                succeeded++;
            }
            catch (DirectRssException ex)
            {
                failed++;
                feeds.RecordFetch(feed.Id, 0, ex.Message, false);
                activity.Add(sourceId, "warn", "sync", $"RSS feed #{feed.Id}: {ex.Message}");
            }
        }
        if (failed > 0 && succeeded == 0) throw new DirectRssException("All enabled RSS feeds failed");
        if (failed > 0) activity.Add(sourceId, "warn", "sync", $"RSS partial success: {succeeded} feeds succeeded, {failed} failed");
        var merged = DirectRssMerger.Merge(entries, feeds.ExistingCategories(sourceId));
        if (merged.Conflicts.Count > 0)
            activity.Add(sourceId, "warn", "sync", $"RSS category conflicts: {merged.Conflicts.Count}; existing assignments preserved, ambiguous new releases skipped");
        return merged with { FailedFeedCount = failed };
    }

    public static Dictionary<int, (string key, string label)> CategoryMap() => CategoryGroupCatalog.CanonicalKeys
        .Order(StringComparer.Ordinal).Select((key, index) => (key, id: 900 + index))
        .ToDictionary(x => x.id, x => (x.key, CategoryGroupCatalog.LabelForKey(x.key)));

    public static List<TorznabItem> Map(DirectRssMergeResult merged)
    {
        var ids = CategoryMap().ToDictionary(x => x.Value.key, x => x.Key);
        return merged.Entries.Select(entry =>
        {
            entry.Item.CategoryId = ids[entry.CategoryKey];
            entry.Item.CategoryIds = new() { entry.Item.CategoryId.Value };
            return entry.Item;
        }).ToList();
    }

    public void RecordProvenance(long sourceId, DirectRssMergeResult merged) => feeds.SaveProvenance(sourceId, merged.Entries);
}
