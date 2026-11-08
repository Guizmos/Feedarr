namespace Feedarr.Api.Options;

/// <summary>Startup-only exceptions for integration transports; empty by default.</summary>
public sealed class OutboundSecurityOptions
{
    public Dictionary<string, string> TrustedHosts { get; set; } = new();
    public List<string> TrustedNetworks { get; set; } = new();
}
