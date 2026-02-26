using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Feedarr.Api.Services.Resilience;

namespace Feedarr.Api.Tests;

public sealed class ResilienceHandlersTests
{
    private static readonly HttpRequestOptionsKey<string> RetryOptionKey = new("feedarr.test.retry.option");

    [Fact]
    public async Task TransientRetry_ReplaysRequestWithContent_UsingFreshRequestPerAttempt()
    {
        var transport = new RecordingSequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        using var retry = new TransientHttpRetryHandler
        {
            InnerHandler = transport
        };
        using var invoker = new HttpMessageInvoker(retry);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/retry")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
            Content = new StringContent("{\"hello\":\"world\"}", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Test", "one");
        request.Options.Set(RetryOptionKey, "retry-opt");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, transport.Requests.Count);

        var first = transport.Requests[0];
        var second = transport.Requests[1];

        Assert.Equal(HttpMethod.Get, second.Method);
        Assert.Equal("https://example.test/retry", second.Uri?.ToString());
        Assert.Equal(HttpVersion.Version20, second.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrHigher, second.VersionPolicy);
        Assert.Equal(first.Body, second.Body);
        Assert.Equal("retry-opt", second.RetryOptionValue);
        Assert.Contains("one", second.HeaderValues("X-Test"));
        Assert.Equal("application/json; charset=utf-8", second.ContentType);
    }

    [Fact]
    public async Task ProtocolRedirect_DoesNotFollowCrossHostRedirect()
    {
        var transport = new RecordingSequenceHandler(
            _ => Redirect("https://evil.test/steal"),
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        using var handler = new ProtocolDowngradeRedirectHandler
        {
            InnerHandler = transport
        };
        using var invoker = new HttpMessageInvoker(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://source.test/start");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Headers.TryAddWithoutValidation("X-Api-Key", "super-secret");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(transport.Requests);
        Assert.Equal("source.test", transport.Requests[0].Uri?.Host);
    }

    [Fact]
    public async Task ProtocolRedirect_SameHostDowngradeAllowed_StripsSensitiveHeaders()
    {
        var transport = new RecordingSequenceHandler(
            _ => Redirect("http://source.test/next"),
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        using var handler = new ProtocolDowngradeRedirectHandler
        {
            InnerHandler = transport
        };
        using var invoker = new HttpMessageInvoker(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://source.test/start");
        request.Options.Set(ProtocolDowngradeRedirectHandler.AllowHttpsToHttpDowngradeOption, true);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Headers.TryAddWithoutValidation("X-Api-Key", "super-secret");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("Feedarr/1.0");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, transport.Requests.Count);

        var redirected = transport.Requests[1];
        Assert.Equal("source.test", redirected.Uri?.Host);
        Assert.Equal(Uri.UriSchemeHttp, redirected.Uri?.Scheme);
        Assert.False(redirected.Headers.ContainsKey("Authorization"));
        Assert.False(redirected.Headers.ContainsKey("X-Api-Key"));
        Assert.True(redirected.Headers.ContainsKey("Accept"));
        Assert.True(redirected.Headers.ContainsKey("User-Agent"));
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.Absolute);
        return response;
    }

    private sealed class RecordingSequenceHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

        public RecordingSequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        {
            _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);
        }

        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers
                .ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase);

            request.Options.TryGetValue(RetryOptionKey, out var retryOptionValue);

            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var contentType = request.Content?.Headers.ContentType?.ToString();

            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri,
                request.Version,
                request.VersionPolicy,
                headers,
                retryOptionValue,
                body,
                contentType));

            if (_responses.Count == 0)
                return new HttpResponseMessage(HttpStatusCode.OK);

            return _responses.Dequeue().Invoke(request);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri? Uri,
        Version Version,
        HttpVersionPolicy VersionPolicy,
        IReadOnlyDictionary<string, string[]> Headers,
        string? RetryOptionValue,
        string? Body,
        string? ContentType)
    {
        public IEnumerable<string> HeaderValues(string key)
            => Headers.TryGetValue(key, out var values) ? values : Array.Empty<string>();
    }
}
