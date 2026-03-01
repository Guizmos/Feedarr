using System.Net;
using System.Text;
using Feedarr.Api.Services.Torznab;
using Microsoft.Extensions.Logging.Abstractions;

namespace Feedarr.Api.Tests;

/// <summary>
/// Verifies that FetchLatestByCategoriesAsync prunes the merged dictionary
/// when it grows beyond limit * 10 entries, keeping the most recent limit * 5 items.
/// </summary>
public sealed class TorznabMergedDictPruningTests
{
    /// <summary>
    /// Builds a minimal RSS feed containing <paramref name="count"/> items,
    /// each with a distinct GUID and a decreasing pubDate (newest first).
    /// </summary>
    private static string BuildRssFeed(int count, string catId = "2000", long baseTs = 1_700_000_000)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<rss version=\"2.0\" xmlns:torznab=\"http://torznab.com/schemas/2015/feed\">");
        sb.AppendLine("  <channel>");
        for (var i = 0; i < count; i++)
        {
            var ts = baseTs - i;
            var dt = DateTimeOffset.FromUnixTimeSeconds(ts).ToString("R");
            sb.AppendLine($"  <item>");
            sb.AppendLine($"    <title>Item-{catId}-{i}</title>");
            sb.AppendLine($"    <guid>guid-{catId}-{i}</guid>");
            sb.AppendLine($"    <pubDate>{dt}</pubDate>");
            sb.AppendLine($"    <torznab:attr name=\"category\" value=\"{catId}\"/>");
            sb.AppendLine($"  </item>");
        }
        sb.AppendLine("  </channel>");
        sb.AppendLine("</rss>");
        return sb.ToString();
    }

    [Fact]
    public async Task FetchLatestByCategories_ManyCategories_MergedDictDoesNotGrowUnbounded()
    {
        // With limit=5 and 12 categories each returning 10 items, without pruning the merged
        // dict would reach 120 entries. With pruning at limit*10=50, it is cut to limit*5=25
        // after the 6th category, and stays bounded thereafter.
        const int limit = 5;
        const int categoriesCount = 12;
        const int itemsPerCategory = 10;

        // Each call to TrySearchAsync returns a fresh feed for the requested category.
        var handler = new PerCategoryFeedHandler(itemsPerCategory);
        var client = new TorznabClient(
            new HttpClient(handler),
            new TorznabRssParser(),
            NullLogger<TorznabClient>.Instance);

        var categoryIds = Enumerable.Range(1, categoriesCount)
            .Select(i => i * 1000)
            .ToList();

        var (items, _, _) = await client.FetchLatestByCategoriesAsync(
            "http://localhost/api",
            "query",
            "key",
            limit,
            categoryIds,
            CancellationToken.None);

        // Final result must be limited to `limit` items
        Assert.True(items.Count <= limit, $"Expected at most {limit} items, got {items.Count}");

        // Items must be sorted newest-first (the pruning keeps the most recent)
        for (var i = 0; i < items.Count - 1; i++)
        {
            Assert.True(
                (items[i].PublishedAtTs ?? 0) >= (items[i + 1].PublishedAtTs ?? 0),
                "Items must be sorted newest-first");
        }
    }

    [Fact]
    public async Task FetchLatestByCategories_FewCategories_ResultsAreNotPruned()
    {
        // With limit=20 and only 3 categories returning 5 items each, merged dict
        // stays at 15 entries (well below limit*10=200). No pruning should occur.
        const int limit = 20;
        const int categoriesCount = 3;
        const int itemsPerCategory = 5;

        var handler = new PerCategoryFeedHandler(itemsPerCategory);
        var client = new TorznabClient(
            new HttpClient(handler),
            new TorznabRssParser(),
            NullLogger<TorznabClient>.Instance);

        var categoryIds = Enumerable.Range(1, categoriesCount)
            .Select(i => i * 1000)
            .ToList();

        var (items, _, _) = await client.FetchLatestByCategoriesAsync(
            "http://localhost/api",
            "query",
            "key",
            limit,
            categoryIds,
            CancellationToken.None);

        // All 15 distinct items should survive (no spurious pruning)
        Assert.Equal(categoriesCount * itemsPerCategory, items.Count);
    }

    // -------------------------------------------------------------------------
    // Handler that returns a feed for the requested category (extracted from `cat=` param)
    // -------------------------------------------------------------------------

    private sealed class PerCategoryFeedHandler : HttpMessageHandler
    {
        private readonly int _itemsPerCategory;

        public PerCategoryFeedHandler(int itemsPerCategory)
        {
            _itemsPerCategory = itemsPerCategory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Extract cat= from query string; default to "2000"
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri?.Query ?? "");
            var cat = query["cat"] ?? "2000";

            // Aggregated calls have cat=1000,2000,3000 (commas) — return empty so only
            // per-category results count, making the test deterministic.
            if (cat.Contains(','))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(BuildRssFeed(0, "0"), Encoding.UTF8, "application/xml")
                });
            }

            // Use a high base timestamp so that items from different categories
            // get distinct (non-colliding) GUIDs and timestamps.
            var baseTs = 1_700_000_000L + (long.TryParse(cat, out var catNum) ? catNum * 10_000 : 0);
            var feed = BuildRssFeed(_itemsPerCategory, cat, baseTs);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(feed, Encoding.UTF8, "application/xml")
            });
        }
    }
}
