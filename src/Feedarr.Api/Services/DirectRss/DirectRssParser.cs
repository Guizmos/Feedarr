using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Torznab;

namespace Feedarr.Api.Services.DirectRss;

/// <summary>RSS 2.0 adapter. Feed categories are authoritative; feed URLs never become identities.</summary>
public sealed class DirectRssParser
{
    public List<DirectRssEntry> Parse(string xml, long feedId, string categoryKey)
        => Parse(XmlSecureParser.Parse(xml), feedId, categoryKey);

    public List<DirectRssEntry> Parse(Stream xml, long feedId, string categoryKey)
        => Parse(XmlSecureParser.Parse(xml), feedId, categoryKey);

    private static List<DirectRssEntry> Parse(XDocument document, long feedId, string categoryKey)
    {
        if (!Categories.CategoryGroupCatalog.TryNormalizeKey(categoryKey, out var category))
            throw new ArgumentException("Invalid Feedarr category");
        if (document.Root?.Name != "rss" || (string?)document.Root.Attribute("version") != "2.0" ||
            document.Root.Element("channel") is null)
            throw new XmlException("Expected RSS 2.0 channel");

        var result = new List<DirectRssEntry>();
        foreach (var node in document.Root.Element("channel")!.Elements("item"))
        {
            string? Value(string name) => node.Elements().FirstOrDefault(e =>
                e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim();
            long? Number(string? value) => long.TryParse(value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : null;
            int? Count(string? value) => Number(value) is long n && n <= int.MaxValue ? (int)n : null;
            string? Attribute(string name) => node.Elements().FirstOrDefault(e => e.Name.LocalName == "attr" &&
                string.Equals((string?)e.Attribute("name"), name, StringComparison.OrdinalIgnoreCase))?.Attribute("value")?.Value;
            var title = Value("title") ?? "";
            if (string.IsNullOrWhiteSpace(title)) continue;
            var guid = Value("guid");
            // A signed link is neither stable nor safe as an identity. Keep explicit GUIDs opaque.
            if (string.IsNullOrWhiteSpace(guid))
                guid = "rss-sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    title + "\n" + (Value("pubDate") ?? "")))).ToLowerInvariant();
            var enclosure = node.Element("enclosure");
            var link = SafeUrl(Value("link"));
            var download = SafeUrl((string?)enclosure?.Attribute("url")) ?? link;
            var page = TrackerPageUrlQualifier.Qualify(SafeUrl(Value("comments")))
                ?? TrackerPageUrlQualifier.Qualify(enclosure is not null ? link : null);
            var description = Value("description");
            var text = System.Net.WebUtility.HtmlDecode(Regex.Replace(description ?? "", "<[^>]*>", " ",
                RegexOptions.None, TimeSpan.FromMilliseconds(100)));
            int? DescriptionCount(string field)
            {
                var match = Regex.Match(text, @"\b" + field + @"s?\s*[:=]?\s*(\d+)",
                    RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
                return match.Success ? Count(match.Groups[1].Value) : null;
            }
            var item = new TorznabItem
            {
                Guid = guid, PreserveGuid = true, Title = title, Description = description,
                Link = link, DownloadUrl = download, TrackerPageUrl = page,
                PublishedAtTs = DateTimeOffset.TryParse(Value("pubDate"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var date) ? date.ToUnixTimeSeconds() : null,
                SizeBytes = Number(Value("size")) ?? Number(Attribute("size")) ?? Number((string?)enclosure?.Attribute("length")),
                Seeders = Count(Value("seeders")) ?? Count(Value("seeds")) ?? Count(Attribute("seeders")) ?? DescriptionCount("seeder"),
                Leechers = Count(Value("leechers")) ?? Count(Attribute("leechers")) ?? DescriptionCount("leecher"),
                Grabs = Count(Value("grabs")) ?? Count(Attribute("grabs")),
                InfoHash = Value("infohash") ?? Attribute("infohash")
            };
            result.Add(new(item, category, new[] { feedId }));
        }
        return result;
    }

    private static string? SafeUrl(string? value) =>
        Security.OutboundUrlGuard.TryNormalizeDownloadUrl(value, out var url, out _) ? url : null;
}

public sealed record DirectRssEntry(TorznabItem Item, string CategoryKey, IReadOnlyList<long> FeedIds);
public sealed record DirectRssMergeResult(IReadOnlyList<DirectRssEntry> Entries, IReadOnlyList<DirectRssConflict> Conflicts, int FailedFeedCount = 0);
public sealed record DirectRssConflict(string Guid, IReadOnlyList<long> FeedIds);

public static class DirectRssMerger
{
    public static DirectRssMergeResult Merge(IEnumerable<DirectRssEntry> entries,
        IReadOnlyDictionary<string, string>? existingCategories = null)
    {
        var merged = new List<DirectRssEntry>();
        var conflicts = new List<DirectRssConflict>();
        foreach (var group in entries.GroupBy(x => x.Item.Guid, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var feeds = group.SelectMany(e => e.FeedIds).Distinct().Order().ToArray();
            var categories = group.Select(e => e.CategoryKey).Distinct(StringComparer.Ordinal).ToArray();
            string? existing = null;
            existingCategories?.TryGetValue(group.Key, out existing);
            if (categories.Length > 1 || (existing is not null && categories.Any(c => c != existing)))
            {
                conflicts.Add(new(group.Key, feeds));
                if (existing is null) continue;
            }
            var chosen = group.Where(e => existing is null || e.CategoryKey == existing)
                .OrderByDescending(e => e.Item.PublishedAtTs)
                .ThenBy(e => e.FeedIds.Min()).ThenBy(e => e.Item.Title, StringComparer.Ordinal)
                .ThenBy(e => e.Item.DownloadUrl, StringComparer.Ordinal).FirstOrDefault();
            if (chosen is not null) merged.Add(chosen with { FeedIds = feeds });
        }
        return new(merged, conflicts);
    }
}
