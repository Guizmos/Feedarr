using System.Xml.Linq;
using Feedarr.Api.Services.DirectRss;

namespace Feedarr.Api.Tests;

public sealed class DirectRssFixtureTests
{
    [Theory]
    [InlineData("tr4ker-films.xml", "films")]
    [InlineData("tr4ker-animation.xml", "animation")]
    [InlineData("tr4ker-general.xml", "films")]
    [InlineData("tr4ker-spectacle.xml", "spectacle")]
    public void ParsesEveryItemAndExtensionFromTheFourOriginalFixtures(string filename, string category)
    {
        var xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "DirectRss", filename));
        var entries = new DirectRssParser().Parse(xml, 42, category);
        Assert.Equal(100, entries.Count);
        var original = XDocument.Parse(xml).Descendants("item").ToList();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            Assert.Equal(category, entry.CategoryKey);
            Assert.Equal(original[i].Element("guid")!.Value.Trim(), entry.Item.Guid);
            Assert.Equal(original[i].Element("title")!.Value.Trim(), entry.Item.Title);
            Assert.Equal(original[i].Element("enclosure")!.Attribute("url")!.Value, entry.Item.DownloadUrl);
            Assert.Equal(original[i].Element("link")!.Value, entry.Item.TrackerPageUrl);
            Assert.NotNull(entry.Item.PublishedAtTs);
            Assert.True(entry.Item.SizeBytes > 0);
            Assert.NotNull(entry.Item.Seeders);
            Assert.NotNull(entry.Item.Leechers);
        }
        var changed = new DirectRssParser().Parse(xml.Replace("passkey=xxx", "passkey=changed"), 42, category);
        Assert.Equal(entries.Select(x => x.Item.Guid), changed.Select(x => x.Item.Guid));
        var merged = DirectRssMerger.Merge(entries.Concat(changed));
        Assert.Equal(entries.Select(x => x.Item.Guid).Distinct().Count(), merged.Entries.Count);
    }

    [Fact]
    public void AllFourFixturesMergeIndependentlyOfReadOrderAndReportCategoryConflicts()
    {
        var parser = new DirectRssParser();
        var entries = new[] { ("films", "films"), ("animation", "animation"), ("general", "films"), ("spectacle", "spectacle") }
            .SelectMany((f, i) => parser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "DirectRss", $"tr4ker-{f.Item1}.xml")), i + 1, f.Item2)).ToList();
        var result = DirectRssMerger.Merge(entries);
        var reversed = DirectRssMerger.Merge(entries.AsEnumerable().Reverse());
        Assert.Equal(result.Entries.Select(e => (e.Item.Guid, e.CategoryKey)), reversed.Entries.Select(e => (e.Item.Guid, e.CategoryKey)));
        Assert.Equal(result.Conflicts.Select(e => e.Guid), reversed.Conflicts.Select(e => e.Guid));
        Assert.NotEmpty(result.Conflicts);
        Assert.Equal(result.Entries.Count, result.Entries.Select(e => e.Item.Guid).Distinct().Count());
    }
}
