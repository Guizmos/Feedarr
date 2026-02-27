using System.Net;
using System.Net.Sockets;

namespace Feedarr.Api.Services.Security;

public static class OutboundUrlGuard
{
    // -----------------------------------------------------------------------
    // Blocked IP ranges — comprehensive SSRF deny list
    // -----------------------------------------------------------------------
    private static readonly IReadOnlyList<IpRange> BlockedRanges =
    [
        // IPv4 — private (RFC 1918)
        IpRange.Parse("10.0.0.0/8"),
        IpRange.Parse("172.16.0.0/12"),
        IpRange.Parse("192.168.0.0/16"),

        // IPv4 — loopback (RFC 5735)
        IpRange.Parse("127.0.0.0/8"),

        // IPv4 — link-local, covers ALL cloud metadata endpoints:
        //   169.254.169.254 (AWS/GCP/Azure IMDSv1), 169.254.170.2 (ECS), etc.
        IpRange.Parse("169.254.0.0/16"),

        // IPv4 — "this network" (RFC 1122 §3.2.1.3)
        IpRange.Parse("0.0.0.0/8"),

        // IPv4 — multicast (RFC 5771)
        IpRange.Parse("224.0.0.0/4"),

        // IPv4 — reserved / future use (RFC 1112)
        IpRange.Parse("240.0.0.0/4"),

        // IPv4 — limited broadcast
        IpRange.Parse("255.255.255.255/32"),

        // IPv6 — loopback
        IpRange.Parse("::1/128"),

        // IPv6 — unspecified
        IpRange.Parse("::/128"),

        // IPv6 — link-local (fe80::/10)
        IpRange.Parse("fe80::/10"),

        // IPv6 — Unique Local Address: fc00::/7 covers fc00:: and fd00::
        IpRange.Parse("fc00::/7"),

        // IPv6 — multicast
        IpRange.Parse("ff00::/8"),
    ];

    // Hostname-based deny list: cloud metadata services that may not resolve to
    // link-local IPs in all environments (GCP, Azure internal DNS).
    private static readonly HashSet<string> BlockedMetadataHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "metadata.google.internal",
        "metadata.azure.internal",
        // Alibaba Cloud metadata (100.100.100.200 is also covered by blocked ranges but
        // this hostname guard catches it before DNS resolution).
        "100.100.100.200",
    };

    // -----------------------------------------------------------------------
    // Public entry points
    // -----------------------------------------------------------------------

    public static bool TryNormalizeProviderBaseUrl(
        string providerType,
        string? rawBaseUrl,
        out string normalizedBaseUrl,
        out string error)
    {
        var normalizedType = (providerType ?? string.Empty).Trim().ToLowerInvariant();
        var trimmed = (rawBaseUrl ?? string.Empty).Trim().TrimEnd('/');

        if (normalizedType == "jackett" &&
            trimmed.EndsWith("/api/v2.0", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^"/api/v2.0".Length];
        }

        if (normalizedType == "prowlarr" &&
            trimmed.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^"/api/v1".Length];
        }

        return TryNormalizeHttpBaseUrl(trimmed, allowImplicitHttp: true, out normalizedBaseUrl, out error);
    }

    public static bool TryNormalizeArrBaseUrl(
        string? rawBaseUrl,
        out string normalizedBaseUrl,
        out string error)
    {
        return TryNormalizeHttpBaseUrl(rawBaseUrl, allowImplicitHttp: true, out normalizedBaseUrl, out error);
    }

    public static bool TryNormalizeHttpBaseUrl(
        string? rawBaseUrl,
        bool allowImplicitHttp,
        out string normalizedBaseUrl,
        out string error)
    {
        normalizedBaseUrl = string.Empty;
        error = "baseUrl invalid";

        var raw = (rawBaseUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "baseUrl missing";
            return false;
        }

        if (allowImplicitHttp &&
            !raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = "http://" + raw;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            error = "baseUrl invalid";
            return false;
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "baseUrl must use http or https";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            error = "baseUrl must not contain credentials";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(uri.Query) || !string.IsNullOrWhiteSpace(uri.Fragment))
        {
            error = "baseUrl must not contain query or fragment";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "baseUrl host missing";
            return false;
        }

        if (IsBlockedHost(uri.Host))
        {
            error = "baseUrl host not allowed";
            return false;
        }

        var normalized = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            Path = uri.AbsolutePath.TrimEnd('/')
        };

        normalizedBaseUrl = normalized.Uri.ToString().TrimEnd('/');
        return true;
    }

    public static bool TryNormalizeDownloadUrl(string? rawUrl, out string normalizedUrl, out string error)
    {
        normalizedUrl = string.Empty;
        error = "download_url invalid";

        var trimmed = (rawUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            error = "download_url missing";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            error = "download_url invalid";
            return false;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https" or "magnet"))
        {
            error = "download_url scheme not allowed";
            return false;
        }

        if (scheme is "http" or "https")
        {
            if (!string.IsNullOrWhiteSpace(uri.UserInfo))
            {
                error = "download_url must not contain credentials";
                return false;
            }

            if (string.IsNullOrWhiteSpace(uri.Host))
            {
                error = "download_url host missing";
                return false;
            }
        }

        normalizedUrl = trimmed;
        return true;
    }

    /// <summary>
    /// Resolves all DNS addresses for <paramref name="host"/> and verifies that none fall
    /// within a blocked range. Call this before making outbound HTTP requests to guard against
    /// DNS-rebinding SSRF attacks (a public hostname that resolves to an internal IP).
    /// </summary>
    /// <returns>
    /// <c>(true, null)</c> if all resolved addresses are public;
    /// <c>(false, reason)</c> if any address is blocked or resolution fails.
    /// </returns>
    public static async Task<(bool Allowed, string? Error)> ValidateOutboundHostAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // DNS failure treated as blocked (fail-safe — if we can't resolve, we can't validate)
            return (false, $"host '{host}' could not be resolved: {ex.Message}");
        }

        if (addresses.Length == 0)
            return (false, $"host '{host}' has no DNS records");

        foreach (var ip in addresses)
        {
            if (IsBlockedIp(ip))
                return (false, $"host '{host}' resolves to a blocked IP address ({ip})");
        }

        return (true, null);
    }

    // -----------------------------------------------------------------------
    // Internal helpers — also used by tests
    // -----------------------------------------------------------------------

    internal static bool IsBlockedHost(string host)
    {
        if (BlockedMetadataHosts.Contains(host))
            return true;

        // If the host is a literal IP address, check it directly.
        if (IPAddress.TryParse(host, out var ip))
            return IsBlockedIp(ip);

        // Hostname (non-IP): static check only; DNS-rebinding requires ValidateOutboundHostAsync.
        return false;
    }

    internal static bool IsBlockedIp(IPAddress ip)
    {
        // Normalize IPv4-mapped IPv6 addresses (e.g. ::ffff:192.168.1.1 → 192.168.1.1)
        // before range comparison so the IPv4 rules above apply correctly.
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        foreach (var range in BlockedRanges)
        {
            if (range.Contains(ip))
                return true;
        }

        return false;
    }
}

// ---------------------------------------------------------------------------
// IpRange — CIDR-based containment check for IPv4 and IPv6
// ---------------------------------------------------------------------------

/// <summary>
/// Represents a network in CIDR notation and supports IP containment checks.
/// Works for both IPv4 and IPv6; family mismatch always returns false.
/// </summary>
internal readonly struct IpRange
{
    private readonly byte[] _networkMasked; // network address already AND-masked
    private readonly byte[] _mask;
    private readonly AddressFamily _family;

    private IpRange(IPAddress network, int prefixLength)
    {
        _family = network.AddressFamily;
        var addrBytes = network.GetAddressBytes();
        var totalBits = addrBytes.Length * 8;

        _mask = new byte[addrBytes.Length];
        for (var i = 0; i < totalBits; i++)
        {
            if (i < prefixLength)
                _mask[i / 8] |= (byte)(0x80 >> (i % 8));
        }

        // Pre-compute network & mask so Contains() is a pure compare
        _networkMasked = new byte[addrBytes.Length];
        for (var i = 0; i < addrBytes.Length; i++)
            _networkMasked[i] = (byte)(addrBytes[i] & _mask[i]);
    }

    /// <summary>Parses a CIDR string such as "192.168.0.0/16" or "fe80::/10".</summary>
    public static IpRange Parse(string cidr)
    {
        var slash = cidr.IndexOf('/');
        if (slash < 0)
            throw new ArgumentException($"Invalid CIDR notation (missing '/'): {cidr}", nameof(cidr));

        var ip = IPAddress.Parse(cidr[..slash]);
        var prefix = int.Parse(cidr[(slash + 1)..]);
        var maxPrefix = ip.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        if (prefix < 0 || prefix > maxPrefix)
            throw new ArgumentOutOfRangeException(nameof(cidr), $"Prefix length {prefix} out of range for {ip.AddressFamily}");

        return new IpRange(ip, prefix);
    }

    /// <summary>Returns true if <paramref name="address"/> is within this network.</summary>
    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != _family)
            return false;

        var addrBytes = address.GetAddressBytes();
        for (var i = 0; i < _networkMasked.Length; i++)
        {
            if ((addrBytes[i] & _mask[i]) != _networkMasked[i])
                return false;
        }

        return true;
    }
}
