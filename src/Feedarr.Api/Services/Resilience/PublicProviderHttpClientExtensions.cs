using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Services.Resilience;

internal static class PublicProviderHttpClientExtensions
{
    // Public metadata and image providers never inherit private integration exceptions.
    internal static IHttpClientBuilder UsePublicProviderTransport(this IHttpClientBuilder builder)
        => builder.ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectCallback = (connection, ct) => sp.GetRequiredService<ValidatedOutboundConnector>()
                .ConnectAsync(connection.DnsEndPoint.Host, connection.DnsEndPoint.Port,
                    OutboundDestinationContext.Strict, ct)
        })
        .AddHttpMessageHandler(sp => new ProtocolDowngradeRedirectHandler(
            sp.GetRequiredService<OutboundDestinationPolicy>(), OutboundDestinationContext.Strict,
            allowPublicRedirects: true))
        .AddHttpMessageHandler(sp => new SsrfGuardHandler(
            sp.GetRequiredService<OutboundDestinationPolicy>(), sp.GetRequiredService<IOutboundDnsResolver>(),
            OutboundDestinationContext.Strict));
}
