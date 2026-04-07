using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class SmartAuthMiddlewareTests
{
    [Fact]
    public async Task SmartMode_LocalRequest_DoesNotRequireAuth()
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        fixture.Settings.SaveSecurity(new SecuritySettings { AuthMode = "smart" });

        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/api/sources",
            host: "localhost",
            remoteIp: IPAddress.Loopback);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task SmartMode_ExposedRequest_RequiresAuthAndBlocksWithoutCredentials()
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        fixture.Settings.SaveSecurity(new SecuritySettings { AuthMode = "smart" });

        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/api/sources",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.11"));

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.Contains("security_setup_required", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SmartMode_ExposedRequest_AllowsSetupStateRoute()
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        fixture.Settings.SaveSecurity(new SecuritySettings { AuthMode = "smart" });

        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/api/setup/state",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.11"));

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task StrictMode_AlwaysRequiresAuth()
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        var (hash, salt) = HashPassword("StrongP@ssw0rd!");
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "strict",
            Username = "admin",
            PasswordHash = hash,
            PasswordSalt = salt
        });

        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/api/sources",
            host: "localhost",
            remoteIp: IPAddress.Loopback);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task OpenMode_NeverRequiresAuth()
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        fixture.Settings.SaveSecurity(new SecuritySettings { AuthMode = "open" });

        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/api/sources",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.15"));

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task SmartMode_ForwardedHeaders_AreTreatedAsExposed()
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        fixture.Settings.SaveSecurity(new SecuritySettings { AuthMode = "smart" });

        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/api/sources",
            host: "localhost",
            remoteIp: IPAddress.Loopback,
            headers: new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = "198.51.100.9"
            });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.Contains("security_setup_required", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/manifest.webmanifest")]
    [InlineData("/favicon.ico")]
    [InlineData("/favicon-32.png")]
    [InlineData("/assets/main.js")]
    [InlineData("/service-worker.js")]
    public async Task StrictMode_PublicStaticAssets_DoNotRequireAuth(string path)
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        var (hash, salt) = HashPassword("StrongP@ssw0rd!");
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "strict",
            Username = "admin",
            PasswordHash = hash,
            PasswordSalt = salt
        });

        var (nextCalled, context) = await fixture.InvokeAsync(
            path: path,
            method: "GET",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.16"));

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task BootstrapTokenRoute_RequiresLoopbackOrBootstrapSecret()
    {
        using var fixture = new MiddlewareFixture(new Dictionary<string, string?>
        {
            ["App:Security:BootstrapSecret"] = "test-bootstrap-secret"
        });
        fixture.SetupState.MarkSetupCompleted();
        fixture.Settings.SaveSecurity(new SecuritySettings { AuthMode = "smart" });

        var (nextCalledDenied, denied) = await fixture.InvokeAsync(
            path: "/api/setup/bootstrap-token",
            method: "POST",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.22"));

        Assert.False(nextCalledDenied);
        Assert.Equal(StatusCodes.Status403Forbidden, denied.Response.StatusCode);

        var (nextCalledAllowed, allowed) = await fixture.InvokeAsync(
            path: "/api/setup/bootstrap-token",
            method: "POST",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.22"),
            headers: new Dictionary<string, string>
            {
                [SmartAuthPolicy.BootstrapSecretHeader] = "test-bootstrap-secret"
            });

        Assert.True(nextCalledAllowed);
        Assert.Equal(StatusCodes.Status200OK, allowed.Response.StatusCode);
    }

    [Fact]
    public async Task BootstrapToken_IsReachable_WhenSecuritySetupRequired()
    {
        using var fixture = new MiddlewareFixture(new Dictionary<string, string?>
        {
            ["App:Security:BootstrapSecret"] = "test-bootstrap-secret"
        });
        fixture.SetupState.MarkSetupCompleted();
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "smart",
            PublicBaseUrl = "https://example.com",
            Username = "",
            PasswordHash = "",
            PasswordSalt = ""
        });

        var (nextCalledSensitive, sensitive) = await fixture.InvokeAsync(
            path: "/api/sources",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.33"));
        Assert.False(nextCalledSensitive);
        Assert.Equal(StatusCodes.Status403Forbidden, sensitive.Response.StatusCode);
        var blockedBody = await ReadBodyAsync(sensitive);
        Assert.Contains("security_setup_required", blockedBody, StringComparison.OrdinalIgnoreCase);

        var (nextCalledBootstrap, bootstrap) = await fixture.InvokeAsync(
            path: "/api/setup/bootstrap-token",
            method: "POST",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.33"),
            headers: new Dictionary<string, string>
            {
                [SmartAuthPolicy.BootstrapSecretHeader] = "test-bootstrap-secret"
            });
        Assert.True(nextCalledBootstrap);
        Assert.Equal(StatusCodes.Status200OK, bootstrap.Response.StatusCode);
    }

    [Fact]
    public async Task BootstrapToken_IsSingleUse_OnSecuritySetupRoutes()
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "smart",
            PublicBaseUrl = "https://feedarr.example.com"
        });

        var token = fixture.BootstrapTokens.IssueToken();

        var (firstNextCalled, firstContext) = await fixture.InvokeAsync(
            path: "/api/settings/security",
            method: "GET",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.44"),
            headers: new Dictionary<string, string>
            {
                [SmartAuthPolicy.BootstrapTokenHeader] = token
            });

        Assert.True(firstNextCalled);
        Assert.Equal(StatusCodes.Status200OK, firstContext.Response.StatusCode);

        var (secondNextCalled, secondContext) = await fixture.InvokeAsync(
            path: "/api/settings/security",
            method: "GET",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.44"),
            headers: new Dictionary<string, string>
            {
                [SmartAuthPolicy.BootstrapTokenHeader] = token
            });

        Assert.False(secondNextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, secondContext.Response.StatusCode);
    }

    [Fact]
    public async Task BootstrapToken_ConcurrentRequests_OnlyOneSucceeds()
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "smart",
            PublicBaseUrl = "https://feedarr.example.com"
        });

        var token = fixture.BootstrapTokens.IssueToken();
        var headers = new Dictionary<string, string>
        {
            [SmartAuthPolicy.BootstrapTokenHeader] = token
        };

        var first = fixture.InvokeAsync(
            path: "/api/settings/security",
            method: "GET",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.45"),
            headers: headers);
        var second = fixture.InvokeAsync(
            path: "/api/settings/security",
            method: "GET",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.45"),
            headers: headers);

        var results = await Task.WhenAll(first, second);
        Assert.Equal(1, results.Count(r => r.nextCalled));
        Assert.Equal(1, results.Count(r => r.context.Response.StatusCode == StatusCodes.Status401Unauthorized));
    }

    [Fact]
    public async Task BootstrapToken_ValidButOutOfScopePath_ReturnsScopeExceeded()
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "smart",
            PublicBaseUrl = "https://feedarr.example.com"
        });

        var token = fixture.BootstrapTokens.IssueToken();

        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/api/sources",
            method: "GET",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.46"),
            headers: new Dictionary<string, string>
            {
                [SmartAuthPolicy.BootstrapTokenHeader] = token
            });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.Contains("bootstrap_token_scope_exceeded", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BootstrapTokenRoute_AcceptsLegacyBootstrapSecretHeader()
    {
        using var fixture = new MiddlewareFixture(new Dictionary<string, string?>
        {
            ["App:Security:BootstrapSecret"] = "test-bootstrap-secret"
        });
        fixture.SetupState.MarkSetupCompleted();
        fixture.Settings.SaveSecurity(new SecuritySettings { AuthMode = "smart" });

        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/api/setup/bootstrap-token",
            method: "POST",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.23"),
            headers: new Dictionary<string, string>
            {
                [SmartAuthPolicy.LegacyBootstrapSecretHeader] = "test-bootstrap-secret"
            });

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task BootstrapTokenRoute_LoopbackRequest_IsAllowedWithoutSecretHeader()
    {
        using var fixture = new MiddlewareFixture(new Dictionary<string, string?>
        {
            ["App:Security:BootstrapSecret"] = "loopback-secret"
        });
        fixture.SetupState.MarkSetupCompleted();
        fixture.Settings.SaveSecurity(new SecuritySettings { AuthMode = "smart" });

        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/api/setup/bootstrap-token",
            method: "POST",
            host: "localhost",
            remoteIp: IPAddress.Loopback);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task BootstrapToken_OutOfScopeRequest_DoesNotConsumeToken()
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "smart",
            PublicBaseUrl = "https://feedarr.example.com"
        });

        var token = fixture.BootstrapTokens.IssueToken();

        var (blockedNextCalled, blockedContext) = await fixture.InvokeAsync(
            path: "/api/sources",
            method: "GET",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.47"),
            headers: new Dictionary<string, string>
            {
                [SmartAuthPolicy.BootstrapTokenHeader] = token
            });

        Assert.False(blockedNextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, blockedContext.Response.StatusCode);

        var (allowedNextCalled, allowedContext) = await fixture.InvokeAsync(
            path: "/api/settings/security",
            method: "GET",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.47"),
            headers: new Dictionary<string, string>
            {
                [SmartAuthPolicy.BootstrapTokenHeader] = token
            });

        Assert.True(allowedNextCalled);
        Assert.Equal(StatusCodes.Status200OK, allowedContext.Response.StatusCode);
    }

    [Fact]
    public async Task BootstrapToken_OnAllowedSecurityRoute_SetsBootstrapActiveContextItem()
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "smart",
            PublicBaseUrl = "https://feedarr.example.com"
        });

        var token = fixture.BootstrapTokens.IssueToken();
        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/api/settings/security",
            method: "GET",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.48"),
            headers: new Dictionary<string, string>
            {
                [SmartAuthPolicy.BootstrapTokenHeader] = token
            });

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(context.Items.TryGetValue(BasicAuthMiddleware.BootstrapTokenActiveKey, out var active));
        Assert.True(active is bool b && b);
    }

    [Fact]
    public async Task BootstrapToken_UnknownOutOfScopeToken_Returns401Challenge()
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "smart",
            PublicBaseUrl = "https://feedarr.example.com"
        });

        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/api/sources",
            method: "GET",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.49"),
            headers: new Dictionary<string, string>
            {
                [SmartAuthPolicy.BootstrapTokenHeader] = "unknown-token"
            });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("Basic realm=\"Feedarr\"", context.Response.Headers.WWWAuthenticate.ToString());
    }

    [Fact]
    public async Task SetupNotCompleted_OptionsRequest_IsAllowed()
    {
        using var fixture = new MiddlewareFixture();
        // setup intentionally left incomplete
        fixture.Settings.SaveSecurity(new SecuritySettings { AuthMode = "strict" });

        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/api/sources",
            method: "OPTIONS",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.50"));

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task BasicAuth_ThrottleAfterSixFailures_Returns429WithRetryAfter()
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        var (hash, salt) = HashPassword("StrongP@ssw0rd!");
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "strict",
            Username = "admin",
            PasswordHash = hash,
            PasswordSalt = salt
        });

        for (var i = 0; i < 5; i++)
        {
            var (nextCalled, context) = await fixture.InvokeAsync(
                path: "/api/sources",
                host: "feedarr.example.com",
                remoteIp: IPAddress.Parse("203.0.113.70"),
                headers: new Dictionary<string, string>
                {
                    ["Authorization"] = ToBasicAuth("admin", "wrong-password")
                });

            Assert.False(nextCalled);
            Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        }

        var (throttledNextCalled, throttledContext) = await fixture.InvokeAsync(
            path: "/api/sources",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.70"),
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = ToBasicAuth("admin", "wrong-password")
            });

        Assert.False(throttledNextCalled);
        Assert.Equal(StatusCodes.Status429TooManyRequests, throttledContext.Response.StatusCode);
        Assert.True(throttledContext.Response.Headers.TryGetValue("Retry-After", out var retryAfter));
        Assert.True(int.TryParse(retryAfter.ToString(), out var retryAfterSeconds));
        Assert.True(retryAfterSeconds >= 1);
    }

    [Fact]
    public async Task BasicAuth_SuccessResetsThrottleWindow()
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        var (hash, salt) = HashPassword("StrongP@ssw0rd!");
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "strict",
            Username = "admin",
            PasswordHash = hash,
            PasswordSalt = salt
        });

        for (var i = 0; i < 5; i++)
        {
            await fixture.InvokeAsync(
                path: "/api/sources",
                host: "feedarr.example.com",
                remoteIp: IPAddress.Parse("203.0.113.71"),
                headers: new Dictionary<string, string>
                {
                    ["Authorization"] = ToBasicAuth("admin", "wrong-password")
                });
        }

        var (_, throttled) = await fixture.InvokeAsync(
            path: "/api/sources",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.71"),
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = ToBasicAuth("admin", "wrong-password")
            });
        Assert.Equal(StatusCodes.Status429TooManyRequests, throttled.Response.StatusCode);

        fixture.Advance(TimeSpan.FromSeconds(6));

        var (successNextCalled, success) = await fixture.InvokeAsync(
            path: "/api/sources",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.71"),
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = ToBasicAuth("admin", "StrongP@ssw0rd!")
            });
        Assert.True(successNextCalled);
        Assert.Equal(StatusCodes.Status200OK, success.Response.StatusCode);

        var (_, afterReset) = await fixture.InvokeAsync(
            path: "/api/sources",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.71"),
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = ToBasicAuth("admin", "wrong-password")
            });
        Assert.Equal(StatusCodes.Status401Unauthorized, afterReset.Response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Fix: constant-time username comparison (FixedTimeEquals)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task BasicAuth_WrongUsername_CorrectPassword_Returns401()
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        var (hash, salt) = HashPassword("CorrectPass!");
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "strict",
            Username = "admin",
            PasswordHash = hash,
            PasswordSalt = salt
        });

        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/api/sources",
            host: "feedarr.example.com",
            remoteIp: System.Net.IPAddress.Parse("203.0.113.99"),
            headers: new Dictionary<string, string>
            {
                // correct password, WRONG username
                ["Authorization"] = ToBasicAuth("notadmin", "CorrectPass!")
            });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task BasicAuth_PrefixUsername_Returns401()
    {
        // "admi" is a prefix of "admin" — FixedTimeEquals must not match partial bytes
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        var (hash, salt) = HashPassword("CorrectPass!");
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "strict",
            Username = "admin",
            PasswordHash = hash,
            PasswordSalt = salt
        });

        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/api/sources",
            host: "feedarr.example.com",
            remoteIp: System.Net.IPAddress.Parse("203.0.113.98"),
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = ToBasicAuth("admi", "CorrectPass!")
            });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Fix 2: Fixed 512-byte buffer — very long username must never throw or match
    // -------------------------------------------------------------------------

    [Fact]
    public async Task BasicAuth_VeryLongUsername_Returns401WithoutException()
    {
        // A username encoded to > 512 UTF-8 bytes must still return 401 cleanly.
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        var (hash, salt) = HashPassword("P@ssw0rd!");
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "strict",
            Username = "admin",
            PasswordHash = hash,
            PasswordSalt = salt
        });

        var veryLongUsername = new string('x', 600); // 600 ASCII bytes > 512 fixed buffer

        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/api/sources",
            host: "feedarr.example.com",
            remoteIp: System.Net.IPAddress.Parse("203.0.113.97"),
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = ToBasicAuth(veryLongUsername, "P@ssw0rd!")
            });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task StrictMode_InvalidAuthorizationHeader_Returns401WithChallenge()
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        var (hash, salt) = HashPassword("StrongP@ssw0rd!");
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "strict",
            Username = "admin",
            PasswordHash = hash,
            PasswordSalt = salt
        });

        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/api/sources",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.88"),
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = "Basic !!!not-base64!!!"
            });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("Basic realm=\"Feedarr\"", context.Response.Headers.WWWAuthenticate.ToString());
    }

    [Fact]
    public async Task StrictMode_BasicAuthorizationWithoutColon_Returns401WithChallenge()
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        var (hash, salt) = HashPassword("StrongP@ssw0rd!");
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "strict",
            Username = "admin",
            PasswordHash = hash,
            PasswordSalt = salt
        });

        var malformedPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes("admin-only"));
        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/api/sources",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.87"),
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = $"Basic {malformedPayload}"
            });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("Basic realm=\"Feedarr\"", context.Response.Headers.WWWAuthenticate.ToString());
    }

    [Fact]
    public async Task StrictMode_BearerAuthorization_IsRejectedWithBasicChallenge()
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        var (hash, salt) = HashPassword("StrongP@ssw0rd!");
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "strict",
            Username = "admin",
            PasswordHash = hash,
            PasswordSalt = salt
        });

        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/api/sources",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.89"),
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer token"
            });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("Basic realm=\"Feedarr\"", context.Response.Headers.WWWAuthenticate.ToString());
    }

    [Fact]
    public async Task StrictMode_HeadStaticAsset_DoesNotRequireAuth()
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        var (hash, salt) = HashPassword("StrongP@ssw0rd!");
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "strict",
            Username = "admin",
            PasswordHash = hash,
            PasswordSalt = salt
        });

        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/favicon.ico",
            method: "HEAD",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.90"));

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task StrictMode_PostOnAssetsPath_DoesNotBypassAuth()
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        var (hash, salt) = HashPassword("StrongP@ssw0rd!");
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "strict",
            Username = "admin",
            PasswordHash = hash,
            PasswordSalt = salt
        });

        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/assets/main.js",
            method: "POST",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.91"));

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task StrictMode_ValidBootstrapSecretHeader_BypassesCredentialCheck()
    {
        using var fixture = new MiddlewareFixture(new Dictionary<string, string?>
        {
            ["App:Security:BootstrapSecret"] = "secret-123"
        });
        fixture.SetupState.MarkSetupCompleted();
        var (hash, salt) = HashPassword("StrongP@ssw0rd!");
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "strict",
            Username = "admin",
            PasswordHash = hash,
            PasswordSalt = salt
        });

        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/api/sources",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.92"),
            headers: new Dictionary<string, string>
            {
                [SmartAuthPolicy.BootstrapSecretHeader] = "secret-123"
            });

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task StrictMode_BootstrapTokenHeader_DoesNotBypassWhenAuthIsConfigured()
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        var (hash, salt) = HashPassword("StrongP@ssw0rd!");
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "strict",
            Username = "admin",
            PasswordHash = hash,
            PasswordSalt = salt
        });

        var token = fixture.BootstrapTokens.IssueToken();
        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/api/sources",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.93"),
            headers: new Dictionary<string, string>
            {
                [SmartAuthPolicy.BootstrapTokenHeader] = token
            });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task BasicAuth_CorruptedPasswordSalt_FallsThroughToPbkdf2AndLogsWarning()
    {
        // ComputeCredCacheKey gets a non-Base64 salt → FormatException → logged + falls through to PBKDF2.
        // With a corrupted salt, actual PBKDF2 validation still runs and the correct password passes.
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        var (hash, _) = HashPassword("StrongP@ssw0rd!");
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "strict",
            Username = "admin",
            PasswordHash = hash,
            PasswordSalt = "not-valid-base64!!!"   // FormatException inside ComputeCredCacheKey
        });

        var listLog = new CapturingLogger<BasicAuthMiddleware>();

        var nextCalled = false;
        var middleware = new BasicAuthMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, new ConfigurationBuilder().Build());

        var db = CreateDb(new TestWorkspace());
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();
        var settings = new SettingsRepository(db);
        settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "strict",
            Username = "admin",
            PasswordHash = hash,
            PasswordSalt = "not-valid-base64!!!"
        });
        var cache = new MemoryCache(new MemoryCacheOptions());
        var setupState = new SetupStateService(settings, cache);
        setupState.MarkSetupCompleted();

        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Path = "/api/sources";
        httpCtx.Request.Host = new HostString("feedarr.example.com");
        httpCtx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.5");
        httpCtx.Request.Headers["Authorization"] = ToBasicAuth("admin", "StrongP@ssw0rd!");
        httpCtx.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(
            httpCtx,
            settings,
            cache,
            setupState,
            new BootstrapTokenService(),
            new AuthThrottleService(new BasicAuthThrottleOptions(), TimeProvider.System),
            listLog);

        Assert.True(listLog.Contains(LogLevel.Warning, "not valid Base64"));
        // Despite the corrupted salt, PBKDF2 validation runs with the correct password → should pass.
        // (The test validates the warning path; auth outcome depends on whether PasswordHash was derived
        //  from the same salt bytes — since the salt stored in DB is corrupt the PBKDF2 check will fail,
        //  but the log warning must have been emitted.)
        Assert.False(nextCalled); // PBKDF2 fails because hash was derived from different salt bytes
    }

    [Fact]
    public async Task StrictMode_ValidCredentials_SetAuthPassedContextItem()
    {
        using var fixture = new MiddlewareFixture();
        fixture.SetupState.MarkSetupCompleted();
        var (hash, salt) = HashPassword("StrongP@ssw0rd!");
        fixture.Settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "strict",
            Username = "admin",
            PasswordHash = hash,
            PasswordSalt = salt
        });

        var (nextCalled, context) = await fixture.InvokeAsync(
            path: "/api/sources",
            host: "feedarr.example.com",
            remoteIp: IPAddress.Parse("203.0.113.94"),
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = ToBasicAuth("admin", "StrongP@ssw0rd!")
            });

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(context.Items.TryGetValue(BasicAuthMiddleware.AuthPassedKey, out var authPassed));
        Assert.True(authPassed is bool b && b);
    }

    private static (string hash, string salt) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
        var hash = pbkdf2.GetBytes(32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    private static async Task<string> ReadBodyAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private static string ToBasicAuth(string username, string password)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

    private sealed class MiddlewareFixture : IDisposable
    {
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());
        private readonly IConfiguration _configuration;
        private readonly TestWorkspace _workspace = new();
        private readonly FakeTimeProvider _timeProvider = new();

        public MiddlewareFixture(Dictionary<string, string?>? config = null)
        {
            var db = CreateDb(_workspace);
            new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();
            Settings = new SettingsRepository(db);
            SetupState = new SetupStateService(Settings, _cache);
            BootstrapTokens = new BootstrapTokenService();
            _configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(config ?? new Dictionary<string, string?>())
                .Build();
            AuthThrottle = new AuthThrottleService(new BasicAuthThrottleOptions(), _timeProvider);
        }

        public SettingsRepository Settings { get; }
        public SetupStateService SetupState { get; }
        public BootstrapTokenService BootstrapTokens { get; }
        public AuthThrottleService AuthThrottle { get; }

        public async Task<(bool nextCalled, DefaultHttpContext context)> InvokeAsync(
            string path,
            string method = "GET",
            string host = "localhost",
            IPAddress? remoteIp = null,
            Dictionary<string, string>? headers = null)
        {
            var nextCalled = false;
            var middleware = new BasicAuthMiddleware(context =>
            {
                nextCalled = true;
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            }, _configuration);

            var context = new DefaultHttpContext();
            context.Request.Path = path;
            context.Request.Method = method;
            context.Request.Host = new HostString(host);
            context.Connection.RemoteIpAddress = remoteIp ?? IPAddress.Loopback;
            context.Response.Body = new MemoryStream();

            if (headers is not null)
            {
                foreach (var (key, value) in headers)
                    context.Request.Headers[key] = value;
            }

            await middleware.InvokeAsync(
                context,
                Settings,
                _cache,
                SetupState,
                BootstrapTokens,
                AuthThrottle,
                NullLogger<BasicAuthMiddleware>.Instance);

            return (nextCalled, context);
        }

        public void Advance(TimeSpan by) => _timeProvider.Advance(by);

        public void Dispose()
        {
            _cache.Dispose();
            _workspace.Dispose();
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _entries.Add((logLevel, formatter(state, exception)));

        public bool Contains(LogLevel level, string fragment)
            => _entries.Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan by) => _utcNow = _utcNow.Add(by);
    }

    private static Db CreateDb(TestWorkspace workspace)
    {
        var options = OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        });

        return new Db(options);
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            RootDir = Path.Combine(Path.GetTempPath(), "feedarr-tests", Guid.NewGuid().ToString("N"));
            DataDir = Path.Combine(RootDir, "data");
            Directory.CreateDirectory(DataDir);
        }

        public string RootDir { get; }
        public string DataDir { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootDir))
                    Directory.Delete(RootDir, true);
            }
            catch
            {
            }
        }
    }
}
