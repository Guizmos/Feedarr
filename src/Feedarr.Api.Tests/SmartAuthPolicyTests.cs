using System.Net;
using System.Text.Json.Nodes;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Feedarr.Api.Tests;

public sealed class AuthenticationPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("192.168.1.12")]
    [InlineData("203.0.113.44")]
    public void EveryNetworkRequiresAuthentication(string ip)
    {
        var context = new DefaultHttpContext(); context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        Assert.True(AuthenticationPolicy.IsAuthRequired(context, new SecuritySettings()));
    }

    [Fact]
    public void RecoveryChangesTransportOnly()
    {
        var context = new DefaultHttpContext(); context.Request.Scheme = "http";
        var security = new SecuritySettings { RequireHttps = true };
        Assert.False(AuthenticationPolicy.CanUseTransport(context.Request, security));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Security:AllowHttpRecovery"] = "true" }).Build();
        Assert.True(AuthenticationPolicy.CanUseTransport(context.Request, security, configuration));
        Assert.True(AuthenticationPolicy.IsAuthRequired(context, security));
    }

    [Theory]
    [InlineData("open",false)]
    [InlineData("smart",true)]
    [InlineData("strict",true)]
    public void LegacyArchiveMappingNeverRestoresAnonymousAccess(string legacy,bool requireHttps)
    {
        var document = new JsonObject { ["authMode"] = legacy };
        AuthenticationPolicy.NormalizeSecurityDocument(document);
        Assert.Equal(requireHttps, document["requireHttps"]!.GetValue<bool>());
        Assert.False(document.ContainsKey("authMode"));
    }
}
