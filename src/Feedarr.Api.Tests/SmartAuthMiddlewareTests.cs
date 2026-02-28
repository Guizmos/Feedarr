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
