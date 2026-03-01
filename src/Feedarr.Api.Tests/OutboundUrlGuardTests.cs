using System.Net;
using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Tests;

/// <summary>
/// Validates the SSRF deny list in <see cref="OutboundUrlGuard"/>.
/// Covers RFC 1918, loopback, link-local, IPv6 private ranges, and IPv4-mapped IPv6.
/// </summary>
public sealed class OutboundUrlGuardTests
{
    // -----------------------------------------------------------------------
    // IsBlockedIp — direct range checks
    // -----------------------------------------------------------------------

    [Theory]
    // RFC 1918
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.100.200")]
    // Loopback
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.255")]
    // Link-local (includes all cloud metadata IPs)
    [InlineData("169.254.169.254")]   // AWS/GCP/Azure IMDSv1
    [InlineData("169.254.170.2")]     // AWS ECS
    [InlineData("169.254.0.1")]       // generic link-local
    // "This network"
    [InlineData("0.0.0.1")]
    // Multicast
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.255")]
    // Reserved
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.254")]
    // Broadcast
    [InlineData("255.255.255.255")]
    // IPv6 loopback
    [InlineData("::1")]
    // IPv6 unspecified
    [InlineData("::")]
    // IPv6 link-local
    [InlineData("fe80::1")]
    [InlineData("fe80::abcd:1234")]
    // IPv6 ULA
    [InlineData("fc00::1")]
    [InlineData("fd00::1")]
    [InlineData("fdff:ffff:ffff::1")]
    // IPv6 multicast
    [InlineData("ff00::1")]
    [InlineData("ff02::1")]
    public void IsBlockedIp_PrivateAddress_ReturnsTrue(string ipStr)
    {
        var ip = IPAddress.Parse(ipStr);
        Assert.True(OutboundUrlGuard.IsBlockedIp(ip), $"Expected {ipStr} to be blocked");
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]    // example.com
    [InlineData("2001:4860:4860::8888")] // Google DNS IPv6
    [InlineData("2606:4700:4700::1111")] // Cloudflare DNS IPv6
    public void IsBlockedIp_PublicAddress_ReturnsFalse(string ipStr)
    {
        var ip = IPAddress.Parse(ipStr);
        Assert.False(OutboundUrlGuard.IsBlockedIp(ip), $"Expected {ipStr} to be allowed");
    }

    [Fact]
    public void IsBlockedIp_IPv4MappedToIPv6_BlockedCorrectly()
    {
        // ::ffff:192.168.1.1 is an IPv4-mapped IPv6 address — must be blocked via RFC 1918 rule
        var ip = IPAddress.Parse("::ffff:192.168.1.1");
        Assert.True(ip.IsIPv4MappedToIPv6, "Precondition: address should be IPv4-mapped");
        Assert.True(OutboundUrlGuard.IsBlockedIp(ip));
    }

    [Fact]
    public void IsBlockedIp_IPv4MappedLoopback_Blocked()
    {
        var ip = IPAddress.Parse("::ffff:127.0.0.1");
        Assert.True(OutboundUrlGuard.IsBlockedIp(ip));
    }

    [Fact]
    public void IsBlockedIp_IPv4MappedPublic_NotBlocked()
    {
        var ip = IPAddress.Parse("::ffff:8.8.8.8");
        Assert.False(OutboundUrlGuard.IsBlockedIp(ip));
    }

    // -----------------------------------------------------------------------
    // TryNormalizeHttpBaseUrl — end-to-end URL validation
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("http://192.168.1.10/")]
    [InlineData("http://10.0.0.1:9117")]
    [InlineData("http://172.20.0.5:8080")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://169.254.169.254")]
    [InlineData("http://169.254.10.10")]         // link-local, non-metadata
    [InlineData("http://[fd00::1]/")]            // IPv6 ULA
    [InlineData("http://[::1]/")]                // IPv6 loopback
    [InlineData("http://[fe80::1]/")]            // IPv6 link-local
    public void TryNormalizeHttpBaseUrl_PrivateIpUrl_ReturnsFalse(string url)
    {
        var ok = OutboundUrlGuard.TryNormalizeHttpBaseUrl(url, allowImplicitHttp: false, out _, out var error);
        Assert.False(ok, $"Expected '{url}' to be rejected");
        Assert.Equal("baseUrl host not allowed", error);
    }

    [Theory]
    [InlineData("http://jackett.example.com:9117")]
    [InlineData("https://prowlarr.example.com")]
    [InlineData("http://8.8.8.8")]
    public void TryNormalizeHttpBaseUrl_PublicUrl_ReturnsTrue(string url)
    {
        var ok = OutboundUrlGuard.TryNormalizeHttpBaseUrl(url, allowImplicitHttp: false, out var normalized, out _);
        Assert.True(ok, $"Expected '{url}' to be accepted");
        Assert.False(string.IsNullOrWhiteSpace(normalized));
    }

    [Fact]
    public void TryNormalizeHttpBaseUrl_MetadataHostname_ReturnsFalse()
    {
        var ok = OutboundUrlGuard.TryNormalizeHttpBaseUrl(
            "http://metadata.google.internal/",
            allowImplicitHttp: false,
            out _,
            out var error);

        Assert.False(ok);
        Assert.Equal("baseUrl host not allowed", error);
    }

    [Fact]
    public void TryNormalizeHttpBaseUrl_AlibabaMetadataIp_ReturnsFalse()
    {
        // 100.100.100.200 is Alibaba Cloud metadata; blocked via hostname deny list
        var ok = OutboundUrlGuard.TryNormalizeHttpBaseUrl(
            "http://100.100.100.200/latest/meta-data/",
            allowImplicitHttp: false,
            out _,
            out _);

        Assert.False(ok);
    }

    // -----------------------------------------------------------------------
    // IpRange.Parse — correctness
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("192.168.0.0/16", "192.168.0.1", true)]
    [InlineData("192.168.0.0/16", "192.168.255.255", true)]
    [InlineData("192.168.0.0/16", "192.167.255.255", false)]
    [InlineData("192.168.0.0/16", "193.0.0.0", false)]
    [InlineData("10.0.0.0/8", "10.1.2.3", true)]
    [InlineData("10.0.0.0/8", "11.0.0.0", false)]
    [InlineData("fc00::/7", "fc00::1", true)]
    [InlineData("fc00::/7", "fd00::1", true)]       // fd00:: is within fc00::/7
    [InlineData("fc00::/7", "fe00::1", false)]
    [InlineData("fe80::/10", "fe80::1", true)]
    [InlineData("fe80::/10", "fe80::abcd", true)]
    [InlineData("fe80::/10", "fe81::1", true)]
    [InlineData("fe80::/10", "fec0::1", false)]
    public void IpRange_Contains_MatchesExpectedResult(string cidr, string ip, bool expected)
    {
        var range = IpRange.Parse(cidr);
        var address = IPAddress.Parse(ip);
        Assert.Equal(expected, range.Contains(address));
    }

    [Fact]
    public void IpRange_ContainsIPv4_DoesNotMatchIPv6()
    {
        // IPv4 range must not accidentally match an IPv6 address
        var range = IpRange.Parse("192.168.0.0/16");
        Assert.False(range.Contains(IPAddress.Parse("::ffff:192.168.1.1")));
    }
}
