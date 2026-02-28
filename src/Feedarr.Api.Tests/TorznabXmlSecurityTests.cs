using System.Net;
using System.Text;
using System.Xml;
using Feedarr.Api.Services.Torznab;
using Microsoft.Extensions.Logging.Abstractions;

namespace Feedarr.Api.Tests;

/// <summary>
/// Verifies that TorznabRssParser and TorznabClient reject malformed / malicious XML.
/// </summary>
public sealed class TorznabXmlSecurityTests
{
    // -------------------------------------------------------------------------
    // TorznabRssParser.Parse — DTD / XXE protection
    // -------------------------------------------------------------------------

    [Fact]
    public void TorznabRssParser_Parse_DtdDeclaration_ThrowsXmlException()
    {
        var parser = new TorznabRssParser();
        const string evilXml =
            "<?xml version=\"1.0\"?>" +
            "<!DOCTYPE foo [<!ENTITY bar \"baz\">]>" +
            "<rss version=\"2.0\"><channel></channel></rss>";

        Assert.Throws<XmlException>(() => parser.Parse(evilXml));
    }

    [Fact]
    public void TorznabRssParser_Parse_ValidRss_ReturnsItems()
    {
        var parser = new TorznabRssParser();
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0">
              <channel>
                <title>Test Feed</title>
                <item>
                  <title>Release Alpha</title>
                  <guid>guid-alpha</guid>
                </item>
              </channel>
            </rss>
            """;

        var items = parser.Parse(xml);

        Assert.Single(items);
        Assert.Equal("Release Alpha", items[0].Title);
    }

    // -------------------------------------------------------------------------
    // TorznabClient.FetchCapsAsync — DTD / XXE protection via HTTP handler
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TorznabClient_FetchCapsAsync_DtdXml_ThrowsXmlException()
    {
        const string evilXml =
            "<?xml version=\"1.0\"?>" +
            "<!DOCTYPE caps [<!ENTITY xxe \"evil\">]>" +
            "<caps><server version=\"1.0\"/></caps>";

        var client = new TorznabClient(
            new HttpClient(new StaticResponseHandler(evilXml, "application/xml")),
            new TorznabRssParser(),
            NullLogger<TorznabClient>.Instance);

        await Assert.ThrowsAsync<XmlException>(
            () => client.FetchCapsAsync("http://localhost/api", "query", "key", CancellationToken.None));
    }

    [Fact]
    public async Task TorznabClient_FetchCapsAsync_ValidCapsXml_ReturnsCategories()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <caps>
              <categories>
                <category id="2000" name="Movies">
                  <subcat id="2040" name="Movies/HD"/>
                </category>
              </categories>
            </caps>
            """;

        var client = new TorznabClient(
            new HttpClient(new StaticResponseHandler(xml, "application/xml")),
            new TorznabRssParser(),
            NullLogger<TorznabClient>.Instance);

        var cats = await client.FetchCapsAsync("http://localhost/api", "query", "key", CancellationToken.None);

        Assert.Equal(2, cats.Count);
        Assert.Contains(cats, c => c.id == 2000 && c.name == "Movies" && !c.isSub);
        Assert.Contains(cats, c => c.id == 2040 && c.name == "Movies/HD" && c.isSub && c.parentId == 2000);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly string _contentType;

        public StaticResponseHandler(string body, string contentType)
        {
            _body = body;
            _contentType = contentType;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, _contentType)
            });
    }
}
