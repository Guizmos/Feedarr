using System.Net;
using System.Text;
using Feedarr.Api.Services.Arr;

namespace Feedarr.Api.Tests;

public sealed class RadarrClientTests
{
    [Fact]
    public async Task LookupMovieAsync_FallsBackToTmdbEndpoint_WhenLegacyLookupFails()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[{\"tmdbId\":1190993,\"title\":\"Flow\",\"titleSlug\":\"flow\",\"year\":2024}]", Encoding.UTF8, "application/json")
            });

        var client = new RadarrClient(new HttpClient(handler));

        var results = await client.LookupMovieAsync("http://localhost:7878", "apikey", 1190993, CancellationToken.None);

        var movie = Assert.Single(results);
        Assert.Equal(1190993, movie.TmdbId);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task LookupMovieAsync_ThrowsOperationalError_WhenAllLookupEndpointsFail()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("bad gateway", Encoding.UTF8, "text/plain")
            },
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("bad gateway", Encoding.UTF8, "text/plain")
            });

        var client = new RadarrClient(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.LookupMovieAsync("http://localhost:7878", "apikey", 1190993, CancellationToken.None));

        Assert.Contains("radarr lookup failed (HTTP 502)", ex.Message);
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public int CallCount { get; private set; }

        public SequenceHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (_responses.Count == 0)
                throw new InvalidOperationException("No response configured.");

            return Task.FromResult(_responses.Dequeue());
        }
    }
}
