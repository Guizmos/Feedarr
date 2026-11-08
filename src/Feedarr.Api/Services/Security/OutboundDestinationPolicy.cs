using System.Net;
using System.Net.Sockets;
using Feedarr.Api.Options;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Services.Security;

public enum OutboundDestinationContext { Strict, TrustedIntegration }

public sealed class OutboundDestinationPolicy
{
    private readonly HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IpRange> _networks = new();
    private static readonly IpRange[] PrivateNetworks =
        [IpRange.Parse("10.0.0.0/8"), IpRange.Parse("172.16.0.0/12"), IpRange.Parse("192.168.0.0/16")];
    private static readonly IpRange[] SpecialNetworks =
        [IpRange.Parse("100.64.0.0/10"), IpRange.Parse("168.63.129.16/32"), IpRange.Parse("192.0.0.0/24"),
         IpRange.Parse("192.0.2.0/24"), IpRange.Parse("192.88.99.0/24"), IpRange.Parse("198.18.0.0/15"),
         IpRange.Parse("198.51.100.0/24"), IpRange.Parse("203.0.113.0/24")];

    public OutboundDestinationPolicy(IOptions<OutboundSecurityOptions> options) : this(options.Value) { }

    public OutboundDestinationPolicy(OutboundSecurityOptions options)
    {
        foreach (var entry in options.TrustedHosts ?? throw new ArgumentException("TrustedHosts must not be null"))
        {
            var value = entry.Value;
            if (string.IsNullOrWhiteSpace(value) || value != value.Trim() ||
                !Uri.TryCreate("http://" + value, UriKind.Absolute, out var uri) ||
                uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 ||
                uri.Fragment.Length != 0 || value.Contains('/') || value.Contains('\\') ||
                !int.TryParse(value[(value.LastIndexOf(':') + 1)..], out var port) || port is < 1 or > 65535 ||
                uri.Port != port || uri.Host.Contains('*') || IsForbiddenHost(uri.IdnHost) ||
                (IPAddress.TryParse(uri.IdnHost, out var ip) && IsAbsolutelyBlocked(ip)))
                throw new ArgumentException($"Invalid App:Security:TrustedHosts:{entry.Key}; expected a safe host:port");
            _hosts.Add(Key(uri.IdnHost, port));
        }
        foreach (var cidr in options.TrustedNetworks ?? throw new ArgumentException("TrustedNetworks must not be null"))
        {
            if (string.IsNullOrWhiteSpace(cidr)) throw new ArgumentException("TrustedNetworks entries must not be empty");
            var parts = cidr.Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) ||
                ip.AddressFamily != AddressFamily.InterNetwork || parts[0] != ip.ToString() || !int.TryParse(parts[1], out var prefix) ||
                prefix is < 0 or > 32)
                throw new ArgumentException($"Invalid RFC1918 CIDR: {cidr}");
            var bytes = ip.GetAddressBytes();
            var last = bytes.ToArray();
            for (var bit = prefix; bit < 32; bit++)
            {
                if ((bytes[bit / 8] & (0x80 >> (bit % 8))) != 0)
                    throw new ArgumentException($"CIDR must use its network address: {cidr}");
                last[bit / 8] |= (byte)(0x80 >> (bit % 8));
            }
            if (!PrivateNetworks.Any(n => n.Contains(ip) && n.Contains(new IPAddress(last))))
                throw new ArgumentException($"CIDR must be wholly RFC1918: {cidr}");
            _networks.Add(IpRange.Parse(cidr));
        }
    }

    private static string Key(string host, int port) => $"{host.ToLowerInvariant()}:{port}";
    internal static bool IsForbiddenHost(string host) =>
        host.TrimEnd('.').Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase) ||
        host.TrimEnd('.').Equals("metadata.azure.internal", StringComparison.OrdinalIgnoreCase);

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    internal static bool IsAbsolutelyBlocked(IPAddress address)
    {
        var ip = Normalize(address);
        if (PrivateNetworks.Any(n => n.Contains(ip))) return false;
        // IPv6 exceptions are never permitted. Only global unicast is eligible;
        // reject transition/documentation space as well as the existing deny list.
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return !IpRange.Parse("2000::/3").Contains(ip) ||
                IpRange.Parse("2001::/23").Contains(ip) || IpRange.Parse("2001:db8::/32").Contains(ip) ||
                IpRange.Parse("2002::/16").Contains(ip) || IpRange.Parse("3fff::/20").Contains(ip);
        return OutboundUrlGuard.IsBlockedIp(ip) || SpecialNetworks.Any(n => n.Contains(ip));
    }

    public bool Allows(string host, int port, IPAddress address, OutboundDestinationContext context)
    {
        var ip = Normalize(address);
        if (IsForbiddenHost(host) || IsAbsolutelyBlocked(ip)) return false;
        if (!PrivateNetworks.Any(n => n.Contains(ip))) return true;
        return context == OutboundDestinationContext.TrustedIntegration &&
            (_hosts.Contains(Key(host, port)) || _networks.Any(n => n.Contains(ip)));
    }
}

internal sealed class OutboundSecurityOptionsValidator : IValidateOptions<OutboundSecurityOptions>
{
    public ValidateOptionsResult Validate(string? name, OutboundSecurityOptions options)
    {
        try { _ = new OutboundDestinationPolicy(options); return ValidateOptionsResult.Success; }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        { return ValidateOptionsResult.Fail(ex.Message); }
    }
}
