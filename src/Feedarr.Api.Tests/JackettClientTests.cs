using System.Net;
using System.Text;
using System.Text.Json;
using Feedarr.Api.Services.Jackett;

namespace Feedarr.Api.Tests;

public sealed class JackettClientTests
{
    [Fact]
    public async Task ListIndexersAsync_WhenIndexersEndpointReturnsJson_ReturnsIndexers()
    {
        var client = new JackettClient(new HttpClient(new JsonIndexersHandler()));

        var list = await client.ListIndexersAsync("https://jackett.example.com", "abc", CancellationToken.None);

        var row = Assert.Single(list);
        Assert.Equal("alpha", row.id);
        Assert.Equal("Alpha", row.name);
        Assert.Equal("https://jackett.example.com/api/v2.0/indexers/alpha/results/torznab/", row.torznabUrl);
    }

    [Fact]
    public async Task ListIndexersAsync_WhenIndexersEndpointReturnsLoginHtml_UsesCapsFallback()
    {
        var client = new JackettClient(new HttpClient(new LoginHtmlThenCapsOkHandler()));

        var list = await client.ListIndexersAsync("https://jackett.example.com/jackett", "abc", CancellationToken.None);

        var row = Assert.Single(list);
        Assert.Equal("all", row.id);
        Assert.Equal("All Indexers", row.name);
        Assert.Equal(
            "https://jackett.example.com/jackett/api/v2.0/indexers/all/results/torznab/api",
            row.torznabUrl);
    }

    [Fact]
    public async Task ListIndexersAsync_WhenIndexersAndCapsFail_Throws()
    {
        var client = new JackettClient(new HttpClient(new LoginHtmlAndCapsHtmlHandler()));

        await Assert.ThrowsAsync<JsonException>(async () =>
            await client.ListIndexersAsync("https://jackett.example.com", "abc", CancellationToken.None));
    }

    private sealed class JsonIndexersHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/api/v2.0/indexers", StringComparison.OrdinalIgnoreCase) == true)
            {
                var payload = """
                    [
                      { "id": "alpha", "name": "Alpha", "configured": true }
                    ]
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class LoginHtmlThenCapsOkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/api/v2.0/indexers", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>login</html>", Encoding.UTF8, "text/html")
                });
            }

            if (path.EndsWith("/api/v2.0/indexers/all/results/torznab/api", StringComparison.OrdinalIgnoreCase))
            {
                var query = request.RequestUri?.Query ?? "";
                if (query.Contains("t=caps", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("apikey=abc", StringComparison.OrdinalIgnoreCase))
                {
                    var xml = """
                        <?xml version="1.0" encoding="UTF-8"?>
                        <caps>
                          <server title="Jackett" />
                        </caps>
                        """;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(xml, Encoding.UTF8, "application/xml")
                    });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class LoginHtmlAndCapsHtmlHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/api/v2.0/indexers", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>login</html>", Encoding.UTF8, "text/html")
                });
            }

            if (path.EndsWith("/api/v2.0/indexers/all/results/torznab/api", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>login</html>", Encoding.UTF8, "text/html")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
