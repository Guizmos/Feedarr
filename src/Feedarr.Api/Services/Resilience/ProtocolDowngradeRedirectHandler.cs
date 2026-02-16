namespace Feedarr.Api.Services.Resilience;

/// <summary>
/// DelegatingHandler that follows HTTP redirects manually, including
/// HTTPS → HTTP downgrades that .NET's HttpClientHandler refuses to follow.
/// Requires AllowAutoRedirect = false on the inner HttpClientHandler.
/// </summary>
internal sealed class ProtocolDowngradeRedirectHandler : DelegatingHandler
{
    private const int MaxRedirects = 5;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);

        for (var i = 0; i < MaxRedirects; i++)
        {
            var sc = (int)response.StatusCode;
            if (sc < 300 || sc >= 400)
                return response;

            var location = response.Headers.Location;
            if (location is null)
                return response;

            var redirectUri = location.IsAbsoluteUri
                ? location
                : new Uri(request.RequestUri!, location);

            response.Dispose();

            using var redirectRequest = new HttpRequestMessage(request.Method, redirectUri);
            // Copy headers (except Host, which is set automatically)
            foreach (var header in request.Headers)
            {
                if (!header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                    redirectRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response = await base.SendAsync(redirectRequest, ct);
        }

        return response;
    }
}
