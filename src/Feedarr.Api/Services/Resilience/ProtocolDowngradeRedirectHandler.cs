using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Services.Resilience;

/// <summary>
/// DelegatingHandler that follows HTTP redirects manually, including
/// HTTPS → HTTP downgrades that .NET's HttpClientHandler refuses to follow.
/// Requires AllowAutoRedirect = false on the primary transport.
/// </summary>
internal sealed class ProtocolDowngradeRedirectHandler : DelegatingHandler
{
    private readonly OutboundDestinationPolicy _policy;
    private readonly OutboundDestinationContext _context;
    private readonly bool _allowPublicRedirects;
    public ProtocolDowngradeRedirectHandler() : this(new OutboundDestinationPolicy(new Feedarr.Api.Options.OutboundSecurityOptions()), OutboundDestinationContext.Strict) { }
    public ProtocolDowngradeRedirectHandler(OutboundDestinationPolicy policy, OutboundDestinationContext context,
        bool allowPublicRedirects = false)
    { _policy = policy; _context = context; _allowPublicRedirects = allowPublicRedirects; }

    internal static readonly HttpRequestOptionsKey<bool> AllowHttpsToHttpDowngradeOption =
        new("feedarr.allow_https_to_http_downgrade_redirect");

    private const int MaxRedirects = 5;
    private static readonly HashSet<string> ForwardHeaderAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        "User-Agent",
        "Accept",
        "Accept-Encoding",
        "Accept-Language"
    };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var currentRequest = request;
        var response = await base.SendAsync(currentRequest, ct).ConfigureAwait(false);
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
                : new Uri(currentRequest.RequestUri!, location);

            if (HasHostChanged(currentRequest.RequestUri, redirectUri) &&
                !(_allowPublicRedirects && _context == OutboundDestinationContext.Strict &&
                  (currentRequest.Method == HttpMethod.Get || currentRequest.Method == HttpMethod.Head)))
                throw BlockedRedirect(response, currentRequest, request);

            // Check literal destinations here. The inner SSRF guard and validated
            // connector reapply DNS policy before sending and opening a connection.
            if ((redirectUri.Scheme != Uri.UriSchemeHttp && redirectUri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(redirectUri.UserInfo) ||
                OutboundDestinationPolicy.IsForbiddenHost(redirectUri.IdnHost) ||
                (System.Net.IPAddress.TryParse(redirectUri.IdnHost, out var address) &&
                 !_policy.Allows(redirectUri.IdnHost, redirectUri.Port, address, _context)))
                throw BlockedRedirect(response, currentRequest, request);

            if (IsHttpsToHttpDowngrade(currentRequest.RequestUri, redirectUri) &&
                (_allowPublicRedirects || !IsHttpsToHttpDowngradeAllowed(currentRequest)))
            {
                throw BlockedRedirect(response, currentRequest, request);
            }

            var redirectRequest = await BuildRedirectRequestAsync(currentRequest, redirectUri, ct).ConfigureAwait(false);
            response.Dispose();
            if (!ReferenceEquals(currentRequest, request))
                currentRequest.Dispose();

            currentRequest = redirectRequest;
            response = await base.SendAsync(currentRequest, ct).ConfigureAwait(false);        }

        return response;
    }

    private static SsrfBlockedException BlockedRedirect(HttpResponseMessage response,
        HttpRequestMessage currentRequest, HttpRequestMessage originalRequest)
    {
        response.Dispose();
        if (!ReferenceEquals(currentRequest, originalRequest)) currentRequest.Dispose();
        return new SsrfBlockedException();
    }

    private static bool HasHostChanged(Uri? sourceUri, Uri redirectUri)
    {
        if (sourceUri is null)
            return true;

        return sourceUri.Port != redirectUri.Port || !string.Equals(sourceUri.IdnHost, redirectUri.IdnHost, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHttpsToHttpDowngrade(Uri? sourceUri, Uri redirectUri)
    {
        if (sourceUri is null)
            return false;

        return sourceUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
               && redirectUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHttpsToHttpDowngradeAllowed(HttpRequestMessage request)
        => request.Options.TryGetValue(AllowHttpsToHttpDowngradeOption, out var allowed) && allowed;

    private static async Task<HttpRequestMessage> BuildRedirectRequestAsync(
        HttpRequestMessage source,
        Uri redirectUri,
        CancellationToken ct)
    {
        var redirectRequest = new HttpRequestMessage(source.Method, redirectUri)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy
        };

        foreach (var option in source.Options)
        {
            redirectRequest.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        }

        foreach (var header in source.Headers)
        {
            if (ForwardHeaderAllowList.Contains(header.Key))
                redirectRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (source.Content is not null)
        {
            var contentBytes = await source.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);            var content = new ByteArrayContent(contentBytes);
            foreach (var header in source.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            redirectRequest.Content = content;
        }

        return redirectRequest;
    }
}
