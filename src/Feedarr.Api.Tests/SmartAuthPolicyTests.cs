using System.Net;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Feedarr.Api.Tests;

public sealed class SmartAuthPolicyTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("192.168.1.2", true)]
    [InlineData("172.17.0.1", true)]
    [InlineData("203.0.113.8", true)]
    [InlineData("2001:db8::8", true)]
    [InlineData("::ffff:203.0.113.8", true)]
    public void Smart_UsesTransportPeer_NotHost(string? peer, bool required)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = peer is null ? null : IPAddress.Parse(peer);
        context.Request.Host = new HostString("localhost");
        Assert.Equal(required, SmartAuthPolicy.IsAuthRequired(context, new SecuritySettings()));
    }

    [Fact]
    public void RewrittenLoopback_CannotOverrideOriginalRemoteConnection()
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("localhost");
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.8");
        var options = new ForwardedHeadersOptions();
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        SmartAuthPolicy.CaptureConnection(context, options);
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        Assert.True(SmartAuthPolicy.IsExposedRequest(context, new SecuritySettings()));
        Assert.False(SmartAuthPolicy.IsLoopbackRequest(context));
    }

    [Theory]
    [InlineData("::ffff:127.0.0.1", "127.0.0.0", 8)]
    [InlineData("127.0.0.1", "::ffff:127.0.0.0", 104)]
    [InlineData("::1", "::1", 128)]
    public void ExplicitProxyNetwork_DoesNotGrantLoopbackExemption(string peer, string network, int prefix)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("localhost");
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        var options = new ForwardedHeadersOptions();
        options.KnownProxies.Clear();
        options.KnownNetworks.Clear();
        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse(network), prefix));
        SmartAuthPolicy.CaptureConnection(context, options);
        Assert.False(SmartAuthPolicy.IsLoopbackRequest(context));
    }
}
