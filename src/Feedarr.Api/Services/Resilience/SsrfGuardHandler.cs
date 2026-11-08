using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Services.Resilience;

/// <summary>
/// Checks every request, including redirects, against the transport's fixed policy.
/// The primary transport must also use ValidatedOutboundConnector: preflight DNS
/// checks alone cannot prevent rebinding between validation and connection.
/// </summary>
internal sealed class SsrfGuardHandler : DelegatingHandler
{
    private readonly OutboundDestinationPolicy _policy;
    private readonly IOutboundDnsResolver _dns;
    private readonly OutboundDestinationContext _context;

    public SsrfGuardHandler() : this(new OutboundDestinationPolicy(new Feedarr.Api.Options.OutboundSecurityOptions()), new OutboundDnsResolver(), OutboundDestinationContext.Strict) { }
    public SsrfGuardHandler(OutboundDestinationPolicy policy, IOutboundDnsResolver dns, OutboundDestinationContext context)
    { _policy = policy; _dns = dns; _context = context; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is not null &&
            (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            var addresses = await _dns.ResolveAsync(uri.IdnHost, cancellationToken).ConfigureAwait(false);
            if (addresses.Length == 0 || addresses.Any(ip => !_policy.Allows(uri.IdnHost, uri.Port, ip, _context)))
                throw new SsrfBlockedException();
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
