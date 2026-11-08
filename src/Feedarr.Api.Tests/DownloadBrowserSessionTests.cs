using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using System.Security.Cryptography;
using System.Text.Json;
using Feedarr.Api.Controllers;
using Feedarr.Api.Middleware;
using Feedarr.Api.Services.Diagnostics;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Feedarr.Api.Tests;

public sealed class DownloadBrowserSessionTests
{
    private readonly EphemeralDataProtectionProvider _protection = new();
    private DownloadBrowserSession Sessions => new(_protection);

    private static DefaultHttpContext Context(string? cookie = null, bool authorized = true, bool https = true)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = https ? "https" : "http";
        context.Request.Method = "GET";
        context.Request.Path = "/api/releases/7/download";
        context.Request.QueryString = new QueryString($"?downloadAttempt={Guid.NewGuid()}");
        if (authorized) context.Items[BasicAuthMiddleware.AuthPassedKey] = true;
        if (cookie is not null) context.Request.Headers.Cookie = cookie;
        return context;
    }

    private string NewCookie(bool https = true)
    {
        var context = Context(https: https);
        Assert.True(Sessions.Prepare(context));
        return context.Response.Headers.SetCookie.ToString().Split(';')[0];
    }

    private static ReleasesController Controller(HttpContext context) => new(
        null!, null!, null!, null!, null!, NullLogger<ReleasesController>.Instance)
    { ControllerContext = new ControllerContext { HttpContext = context } };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CookiesAreServerIssuedProtectedAndBoundToTheHost(bool https)
    {
        var context = Context(https: https);
        Assert.True(Sessions.Prepare(context));
        var header = context.Response.Headers.SetCookie.ToString();
        Assert.Contains("httponly", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", header, StringComparison.OrdinalIgnoreCase);
        if (https)
        {
            Assert.StartsWith("__Host-", header);
            Assert.Contains("secure", header, StringComparison.OrdinalIgnoreCase);
        }
        var request = Context(header.Split(';')[0], https: https);
        Assert.True(Sessions.TryGet(request, out var session));
        Assert.Equal(64, session.Length);
        Assert.DoesNotContain(session, header);
        Assert.True(Sessions.Prepare(request));
        Assert.False(request.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task AnonymousRequestsCannotCreateReadOrOverwriteDiagnosticsEvenWithAValidCookie()
    {
        using var diagnostics = new DownloadDiagnostics();
        var cookie = NewCookie();
        var valid = Context(cookie);
        Assert.True(Sessions.TryGet(valid, out var session));
        var attempt = Guid.NewGuid();
        Assert.True(diagnostics.TryStart(session, 7, attempt));
        diagnostics.Finish(session, 7, attempt, 200, false, null);
        var anonymous = Context(cookie, authorized: false);
        anonymous.Request.QueryString = new QueryString($"?downloadAttempt={attempt}");
        var middleware = new DownloadDiagnosticsMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 401;
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(anonymous, diagnostics, Sessions);
        Assert.False(Sessions.Prepare(anonymous));
        Assert.IsType<UnauthorizedResult>(Controller(anonymous).DownloadStatus(7, attempt, diagnostics, Sessions));
        Assert.IsType<UnauthorizedResult>(Controller(anonymous).PrepareDownloadSession(7, Sessions));
        Assert.False(anonymous.Response.Headers.ContainsKey("Set-Cookie"));
        Assert.Equal("completed", diagnostics.Get(session, 7, attempt)!.State);
        Assert.Equal(1, diagnostics.EntryCount);
    }

    [Fact]
    public async Task SeparateBrowsersCannotReadOrOverwriteEachOtherEvenWithIdenticalAttemptIds()
    {
        using var diagnostics = new DownloadDiagnostics();
        var a = Context(NewCookie());
        var b = Context(NewCookie());
        Assert.True(Sessions.TryGet(a, out var sessionA));
        Assert.True(Sessions.TryGet(b, out var sessionB));
        Assert.NotEqual(sessionA, sessionB);
        var attempt = Guid.NewGuid();
        a.Request.QueryString = b.Request.QueryString = new QueryString($"?downloadAttempt={attempt}");
        var success = new DownloadDiagnosticsMiddleware(_ => Task.CompletedTask);
        await success.InvokeAsync(a, diagnostics, Sessions);
        Assert.IsType<NotFoundObjectResult>(Controller(b).DownloadStatus(7, attempt, diagnostics, Sessions));
        var failure = new DownloadDiagnosticsMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 403;
            return Task.CompletedTask;
        });
        await failure.InvokeAsync(b, diagnostics, Sessions);
        Assert.Equal("completed", diagnostics.Get(sessionA, 7, attempt)!.State);
        Assert.Equal("failed", diagnostics.Get(sessionB, 7, attempt)!.State);
        var result = Assert.IsType<OkObjectResult>(Controller(a).DownloadStatus(7, attempt, diagnostics, Sessions));
        var json = JsonSerializer.Serialize(result.Value);
        Assert.DoesNotContain(sessionA, json);
        Assert.DoesNotContain(a.Request.Headers.Cookie.ToString(), json);
        Assert.Equal("no-store", a.Response.Headers.CacheControl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrExpiredCookiesAreRejectedWithoutAdoptingClientIdentity(bool expired)
    {
        using var diagnostics = new DownloadDiagnostics();
        var token = expired
            ? _protection.CreateProtector(DownloadBrowserSession.Purpose).ToTimeLimitedDataProtector()
                .Protect(new string('A', 64), DateTimeOffset.UtcNow.AddMinutes(-1))
            : "attacker-chosen-identity";
        var context = Context($"__Host-FeedarrDownloadSession={token}");
        Assert.False(Sessions.TryGet(context, out _));
        var middleware = new DownloadDiagnosticsMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(context, diagnostics, Sessions);
        Assert.Equal(0, diagnostics.EntryCount);
        var rejected = Assert.IsType<ObjectResult>(Controller(context).PrepareDownloadSession(7, Sessions));
        Assert.Equal(403, rejected.StatusCode);
        Assert.Contains("download_session_invalid", JsonSerializer.Serialize(rejected.Value));
        Assert.DoesNotContain(token, context.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public void MissingSessionNeverCreatesACookieOnStatusLookup()
    {
        using var diagnostics = new DownloadDiagnostics();
        var context = Context();
        var rejected = Assert.IsType<ObjectResult>(Controller(context).DownloadStatus(7, Guid.NewGuid(), diagnostics, Sessions));
        Assert.Equal(403, rejected.StatusCode);
        // Clearing an absent cookie issues no new credential.
        Assert.DoesNotContain("download session", context.Response.Headers.SetCookie.ToString());
        Assert.Equal(0, diagnostics.EntryCount);
    }

    [Fact]
    public void ExpirationAndBoundedMemoryDoNotResurrectStaleAttempts()
    {
        var clock = new TestClock();
        using var diagnostics = new DownloadDiagnostics(clock);
        var attempt = Guid.NewGuid();
        Assert.True(diagnostics.TryStart("browser", 7, attempt));
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.Null(diagnostics.Get("browser", 7, attempt));
        diagnostics.Finish("browser", 7, attempt, 200, false, null);
        Assert.Null(diagnostics.Get("browser", 7, attempt));
        for (var i = 0; i < 2048; i++) diagnostics.TryStart("browser", i + 1, Guid.NewGuid());
        Assert.InRange(diagnostics.EntryCount, 0, 1024);
    }

    [Fact]
    public async Task ReplayedAttemptCannotOverwriteCompletedStateAndAbortIsNotSuccess()
    {
        using var diagnostics = new DownloadDiagnostics();
        var context = Context(NewCookie());
        Assert.True(Sessions.TryGet(context, out var session));
        var attempt = Guid.NewGuid();
        context.Request.QueryString = new QueryString($"?downloadAttempt={attempt}");
        var middleware = new DownloadDiagnosticsMiddleware(_ => throw new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context, diagnostics, Sessions));
        Assert.Equal("failed", diagnostics.Get(session, 7, attempt)!.State);
        var replay = new DownloadDiagnosticsMiddleware(_ => Task.CompletedTask);
        await replay.InvokeAsync(context, diagnostics, Sessions);
        Assert.Equal("failed", diagnostics.Get(session, 7, attempt)!.State);
        Assert.Equal(1, diagnostics.EntryCount);
    }

    [Fact]
    public async Task TrustedHttpsReverseProxyProducesSecureHostOnlyCookie()
    {
        var context = Context(https: false);
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto };
        options.KnownProxies.Add(IPAddress.Loopback);
        var middleware = new ForwardedHeadersMiddleware(ctx =>
        {
            Assert.True(Sessions.Prepare(ctx));
            return Task.CompletedTask;
        }, NullLoggerFactory.Instance, Microsoft.Extensions.Options.Options.Create(options));
        await middleware.Invoke(context);
        Assert.True(context.Request.IsHttps);
        Assert.StartsWith("__Host-", context.Response.Headers.SetCookie.ToString());
        Assert.Contains("secure", context.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
