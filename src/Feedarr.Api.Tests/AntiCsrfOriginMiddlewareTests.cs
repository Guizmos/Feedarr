using System.IO;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Feedarr.Api.Tests;

public sealed class AntiCsrfOriginMiddlewareTests
{
    [Fact]
    public async Task UnsafeRequest_WithDisallowedOrigin_Returns403()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Security:AllowedOrigins:0"] = "http://localhost:5173"
        });

        var (nextCalled, context) = await InvokeAsync(configuration, new Dictionary<string, string>
        {
            ["Origin"] = "https://evil.example"
        });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task UnsafeRequest_WithAllowedOrigin_PassesThrough()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Security:AllowedOrigins:0"] = "http://localhost:5173"
        });

        var (nextCalled, context) = await InvokeAsync(configuration, new Dictionary<string, string>
        {
            ["Origin"] = "http://localhost:5173"
        });

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task UnsafeRequest_WithoutOriginOrRefererAndWithoutTrustedHeader_Returns403()
    {
        var configuration = BuildConfiguration();

        var (nextCalled, context) = await InvokeAsync(configuration, headers: null);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task UnsafeRequest_WithoutOriginOrReferer_ButWithTrustedHeader_PassesThrough()
    {
        var configuration = BuildConfiguration();

        var (nextCalled, context) = await InvokeAsync(configuration, new Dictionary<string, string>
        {
            [RequestForgeryProtection.RequestHeaderName] = RequestForgeryProtection.RequestHeaderValue
        });

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?>? values = null)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values ?? new Dictionary<string, string?>
            {
                ["Security:RequireXsrfHeaderForUnsafeMethods"] = "true"
            })
            .Build();
    }

    private static async Task<(bool nextCalled, DefaultHttpContext context)> InvokeAsync(
        IConfiguration configuration,
        Dictionary<string, string>? headers)
    {
        var nextCalled = false;
        var middleware = new AntiCsrfOriginMiddleware(context =>
        {
            nextCalled = true;
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/settings/security";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 5003);
        context.Response.Body = new MemoryStream();

        if (headers is not null)
        {
            foreach (var (key, value) in headers)
                context.Request.Headers[key] = value;
        }

        await middleware.InvokeAsync(context, configuration, NullLogger<AntiCsrfOriginMiddleware>.Instance);
        return (nextCalled, context);
    }
}
