using System.Net;
using System.Xml;
using Feedarr.Api.Services.DirectRss;

namespace Feedarr.Api.Tests;

public sealed class DirectRssTests
{
    private const string Xml = """
        <rss version="2.0" xmlns:t="http://torznab.com/schemas/2015/feed"><channel><item>
        <guid>opaque-guid</guid><title>Example</title><pubDate>Wed, 25 Nov 2026 12:00:00 GMT</pubDate>
        <link>https://example.org/details/1</link><enclosure url="https://example.org/download/1?passkey=secret" length="123"/>
        <t:attr name="seeders" value="12"/><leechers>3</leechers><category>99999</category>
        </item></channel></rss>
        """;

    [Fact]
    public void ParsesRssAndUsesConfiguredCategory()
    {
        var entry = Assert.Single(new DirectRssParser().Parse(Xml, 42, "films"));
        Assert.Equal("opaque-guid", entry.Item.Guid);
        Assert.Equal("films", entry.CategoryKey);
        Assert.Equal(new long[] { 42 }, entry.FeedIds);
        Assert.Equal(123, entry.Item.SizeBytes);
        Assert.Equal(12, entry.Item.Seeders);
        Assert.Equal(3, entry.Item.Leechers);
        Assert.NotNull(entry.Item.PublishedAtTs);
        Assert.Equal("https://example.org/details/1", entry.Item.TrackerPageUrl);
        Assert.Contains("passkey=secret", entry.Item.DownloadUrl);
        Assert.Null(entry.Item.CategoryId);
    }

    [Theory]
    [InlineData("<!DOCTYPE rss [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><rss version='2.0'><channel/></rss>")]
    [InlineData("<feed/>")]
    [InlineData("<html/>")]
    public void RejectsUnsafeOrNonRssXml(string xml) => Assert.Throws<XmlException>(() => new DirectRssParser().Parse(xml, 1, "films"));

    [Fact]
    public void EnforcesXmlLimit() => Assert.Throws<XmlException>(() => new DirectRssParser().Parse(
        "<rss version='2.0'><channel>" + new string('x', 5_000_001) + "</channel></rss>", 1, "films"));

    [Fact]
    public void MergeIsOrderIndependentAndPreservesExistingCategory()
    {
        var parser = new DirectRssParser();
        var entries = parser.Parse(Xml, 2, "series").Concat(parser.Parse(Xml, 1, "films")).ToArray();
        Assert.Empty(DirectRssMerger.Merge(entries).Entries);
        var existing = new Dictionary<string, string> { ["opaque-guid"] = "films" };
        var first = DirectRssMerger.Merge(entries, existing);
        var second = DirectRssMerger.Merge(entries.Reverse(), existing);
        Assert.Single(first.Conflicts);
        Assert.Equal("films", Assert.Single(first.Entries).CategoryKey);
        Assert.Equal(first.Entries[0].Item.DownloadUrl, second.Entries[0].Item.DownloadUrl);
        Assert.Equal(new long[] { 1, 2 }, first.Entries[0].FeedIds);
    }

    [Fact]
    public void PasskeyChangesDoNotChangeIdentity()
    {
        var parser = new DirectRssParser();
        foreach (var xml in new[] { Xml, Xml.Replace("<guid>opaque-guid</guid>", "") })
            Assert.Equal(parser.Parse(xml, 1, "films")[0].Item.Guid,
                parser.Parse(xml.Replace("secret", "new-passkey"), 1, "films")[0].Item.Guid);
    }

    [Fact]
    public async Task ClientGetsExactSignedUrlAndMasksErrors()
    {
        var handler = new Handler();
        var client = new DirectRssClient(new HttpClient(handler), new DirectRssParser());
        const string url = "https://example.org/rss?passkey=secret&category=1";
        await client.FetchAsync(url, 1, "films", default);
        Assert.Equal(url, handler.Url);
        handler.Fail = true;
        var error = await Assert.ThrowsAsync<DirectRssException>(() => client.FetchAsync(url, 1, "films", default));
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Theory]
    [InlineData("http://127.0.0.1/rss")]
    [InlineData("http://169.254.169.254/rss")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:secret@example.org/rss")]
    public async Task RejectsUnsafeUrlsBeforeSending(string url)
    {
        var handler = new Handler();
        var client = new DirectRssClient(new HttpClient(handler), new DirectRssParser());
        await Assert.ThrowsAsync<ArgumentException>(() => client.FetchAsync(url, 1, "films", default));
        Assert.Null(handler.Url);
    }

    [Fact]
    public void ByteParserHonorsDeclaredXmlEncoding()
    {
        var xml = "<?xml version='1.0' encoding='iso-8859-1'?><rss version='2.0'><channel><item><guid>id</guid><title>Été</title></item></channel></rss>";
        using var stream = new MemoryStream(System.Text.Encoding.Latin1.GetBytes(xml));
        Assert.Equal("Été", Assert.Single(new DirectRssParser().Parse(stream, 1, "films")).Item.Title);
    }

    [Fact]
    public void SameCategoryDuplicatesMergeWithEveryFeedAndStableSelection()
    {
        var parser = new DirectRssParser();
        var entries = parser.Parse(Xml.Replace("Example", "Older"), 8, "films").Concat(parser.Parse(Xml, 2, "films")).ToArray();
        var a = DirectRssMerger.Merge(entries);
        var b = DirectRssMerger.Merge(entries.Reverse());
        Assert.Empty(a.Conflicts);
        Assert.Single(a.Entries);
        Assert.Equal(a.Entries[0].Item.Title, b.Entries[0].Item.Title);
        Assert.Equal(new long[] { 2, 8 }, a.Entries[0].FeedIds);
    }

    [Fact]
    public async Task DnsAndRedirectGuardsBlockPrivateTargetsWithoutTransportAccess()
    {
        var policy = new Feedarr.Api.Services.Security.OutboundDestinationPolicy(new Feedarr.Api.Options.OutboundSecurityOptions());
        var target = new Handler();
        var guard = new Feedarr.Api.Services.Resilience.SsrfGuardHandler(policy, new PrivateDns(),
            Feedarr.Api.Services.Security.OutboundDestinationContext.Strict) { InnerHandler = target };
        var client = new DirectRssClient(new HttpClient(guard), new());
        await Assert.ThrowsAsync<DirectRssException>(() => client.FetchAsync("https://example.org/rss?secret=hidden", 1, "films", default));
        Assert.Null(target.Url);

        var redirect = new Feedarr.Api.Services.Resilience.ProtocolDowngradeRedirectHandler(policy,
            Feedarr.Api.Services.Security.OutboundDestinationContext.Strict) { InnerHandler = new RedirectHandler() };
        var redirected = new DirectRssClient(new HttpClient(redirect), new());
        var error = await Assert.ThrowsAsync<DirectRssException>(() => redirected.FetchAsync("https://example.org/rss?secret=hidden", 1, "films", default));
        Assert.DoesNotContain("hidden", error.ToString());
    }

    [Fact]
    public async Task ClientBoundsUnknownLengthResponsesAndPropagatesCancellation()
    {
        var client = new DirectRssClient(new HttpClient(new LargeHandler()), new());
        await Assert.ThrowsAsync<DirectRssException>(() => client.FetchAsync("https://example.org/rss", 1, "films", default));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FetchAsync("https://example.org/rss", 1, "films", stop.Token));
    }

    private sealed class PrivateDns : Feedarr.Api.Services.Security.IOutboundDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Task.FromResult(new[] { IPAddress.Loopback });
    }
    private sealed class RedirectHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://127.0.0.1/?secret=hidden") } });
    }
    private sealed class LargeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekingStream(new byte[5_000_001])) });
    }

    private sealed class NonSeekingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string? Url;
        public bool Fail;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Url = request.RequestUri!.AbsoluteUri;
            if (Fail) throw new HttpRequestException(Url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Xml) });
        }
    }
}
