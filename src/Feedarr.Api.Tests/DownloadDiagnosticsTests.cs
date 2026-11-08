using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.DataProtection;
using System.Net;
using System.Text.Json;
using Feedarr.Api.Middleware;
using Feedarr.Api.Services.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Feedarr.Api.Tests;

public sealed class DownloadDiagnosticsTests
{
    private const string Session = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly EphemeralDataProtectionProvider Protection = new();
    private static readonly DownloadBrowserSession Sessions = new(Protection);
    [Theory]
    [InlineData(200, "completed")]
    [InlineData(400, "failed")]
    [InlineData(401, "failed")]
    [InlineData(403, "failed")]
    [InlineData(404, "failed")]
    [InlineData(500, "failed")]
    [InlineData(502, "failed")]
    public async Task ObservesFinalStatusWithoutRepeatingTheRequest(int status, string state)
    {
        using var diagnostics = new DownloadDiagnostics();
        var attempt = Guid.NewGuid();
        var context = Context(attempt);
        var requests = 0;
        var middleware = new DownloadDiagnosticsMiddleware(ctx =>
        {
            requests++;
            ctx.Response.StatusCode = status;
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(context, diagnostics, Sessions);
        for (var i = 0; i < 3; i++)
        {
            var result = diagnostics.Get(Session, 7, attempt);
            Assert.NotNull(result);
            Assert.Equal(state, result.State);
            Assert.Equal(status, result.StatusCode);
        }
        Assert.Equal(1, requests);
        Assert.Equal(status, context.Response.StatusCode);
        Assert.True(context.Items[BasicAuthMiddleware.AuthPassedKey] is true);
    }

    [Fact]
    public async Task StreamingExceptionIsRecordedWithoutExposingItsMessage()
    {
        using var diagnostics = new DownloadDiagnostics();
        var attempt = Guid.NewGuid();
        var middleware = new DownloadDiagnosticsMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 200;
            throw new IOException("https://private/api?apikey=secret");
        });
        await Assert.ThrowsAsync<IOException>(() => middleware.InvokeAsync(Context(attempt), diagnostics, Sessions));
        var result = diagnostics.Get(Session, 7, attempt);
        Assert.NotNull(result);
        Assert.Equal("failed", result.State);
        Assert.Equal("network_error", result.Code);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task PreservesMagnetRedirectWithoutPublishingTheUriInDiagnostics()
    {
        using var diagnostics = new DownloadDiagnostics();
        var attempt = Guid.NewGuid();
        var context = Context(attempt);
        const string magnet = "magnet:?xt=urn:btih:example";
        var middleware = new DownloadDiagnosticsMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 302;
            ctx.Response.Headers.Location = magnet;
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(context, diagnostics, Sessions);
        Assert.Equal("completed", diagnostics.Get(Session, 7, attempt)!.State);
        Assert.Equal(magnet, context.Response.Headers.Location);
        Assert.DoesNotContain("magnet", JsonSerializer.Serialize(diagnostics.Get(Session, 7, attempt)));
    }

    [Fact]
    public async Task TracksLocalSsrfCodeAndSeparatesAttemptsAndReleases()
    {
        using var diagnostics = new DownloadDiagnostics();
        var attempt = Guid.NewGuid();
        var middleware = new DownloadDiagnosticsMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 400;
            ctx.Items[DownloadDiagnostics.FailureCodeKey] = "ssrf_blocked";
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(Context(attempt), diagnostics, Sessions);
        Assert.Equal("ssrf_blocked", diagnostics.Get(Session, 7, attempt)!.Code);
        Assert.Null(diagnostics.Get(Session, 8, attempt));
        Assert.Null(diagnostics.Get(Session, 7, Guid.NewGuid()));
    }

    [Fact]
    public async Task NeverReportsCompletionWhileResponseIsStillStreaming()
    {
        using var diagnostics = new DownloadDiagnostics();
        var attempt = Guid.NewGuid();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new DownloadDiagnosticsMiddleware(_ => finish.Task);
        var request = middleware.InvokeAsync(Context(attempt), diagnostics, Sessions);
        Assert.Equal("pending", diagnostics.Get(Session, 7, attempt)!.State);
        finish.SetResult();
        await request;
        Assert.Equal("completed", diagnostics.Get(Session, 7, attempt)!.State);
    }

    private static DefaultHttpContext Context(Guid attempt)
    {
        var context = new DefaultHttpContext();
        context.Items[BasicAuthMiddleware.AuthPassedKey] = true;
        var token = Protection.CreateProtector(DownloadBrowserSession.Purpose).ToTimeLimitedDataProtector()
            .Protect(Session, TimeSpan.FromHours(1));
        context.Request.Headers.Cookie = $"{DownloadBrowserSession.CookieName(context)}={token}";
        context.Request.Method = "GET";
        context.Request.Path = "/api/releases/7/download";
        context.Request.QueryString = new QueryString($"?downloadAttempt={attempt}");
        return context;
    }
}
