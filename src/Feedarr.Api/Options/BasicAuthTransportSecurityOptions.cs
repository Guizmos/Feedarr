namespace Feedarr.Api.Options;

public sealed class BasicAuthTransportSecurityOptions
{
    public bool? RequireHttpsForBasicAuth { get; set; }
    public bool TrustedReverseProxyTls { get; set; } = false;
    public string[] KnownProxies { get; set; } = [];
    public string[] KnownNetworks { get; set; } = [];
}
