using System.Net;
using System.Text;
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
    public async Task ListIndexersAsync_WhenIndexersEndpointReturnsLoginHtml_UsesTorznabFallback()
    {
        var client = new JackettClient(new HttpClient(new LoginHtmlThenTorznabOkHandler()));

        var list = await client.ListIndexersAsync("https://jackett.example.com/jackett", "abc", CancellationToken.None);

        var row = Assert.Single(list);
        Assert.Equal("beta", row.id);
        Assert.Equal("Beta", row.name);
        Assert.Contains("/api/v2.0/indexers/beta/results/torznab/", row.torznabUrl);
    }

    [Fact]
    public async Task ListIndexersAsync_WhenBothStrategiesFail_ReturnsEmpty()
    {
        var client = new JackettClient(new HttpClient(new LoginHtmlBothHandler()));

        var list = await client.ListIndexersAsync("https://jackett.example.com", "abc", CancellationToken.None);

        Assert.Empty(list);
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

    private sealed class LoginHtmlThenTorznabOkHandler : HttpMessageHandler
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
                var xml = """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <indexers>
                      <indexer id="beta" configured="true">
                        <title>Beta</title>
                      </indexer>
                    </indexers>
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(xml, Encoding.UTF8, "application/xml")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class LoginHtmlBothHandler : HttpMessageHandler
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
