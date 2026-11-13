namespace Feedarr.Api.Options;

public sealed class BasicAuthTransportSecurityOptions
{
    public string[] KnownProxies { get; set; } = [];
    public string[] KnownNetworks { get; set; } = [];
}
