using System.Net;
using System.Text;
using System.Text.Json;
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

// Regression coverage for removal of the setup lock; endpoint authorization stays independent.
public sealed class SetupLockMiddlewareTests
{
    [Theory]
    [InlineData("GET", "/")]
    [InlineData("GET", "/library")]
    [InlineData("GET", "/create-admin")]
    [InlineData("GET", "/health")]
    [InlineData("HEAD", "/assets/main.js")]
    [InlineData("POST", "/api/auth/create-admin")]
    [InlineData("GET", "/api/auth/session")]
    public async Task FreshHttpInstance_PublicRoutesReachTheirHandlersWithoutBootstrap(string method, string path)
    {
        using var f = new Fixture();
        var (context, reached) = await f.Request(method, path);
        Assert.True(reached); // Auth controllers still enforce same-origin, rate limits and atomic creation.
        Assert.Equal(200, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Location"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingAdministrator_PrivateApiIsProtectedRegardlessOfOnboarding(bool onboardingDone)
    {
        using var f = new Fixture(onboardingDone);
        var (context, reached) = await f.Request("GET", "/api/sources");
        Assert.False(reached);
        Assert.Equal(403, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("security_setup_required", body.RootElement.GetProperty("error").GetString());
        Assert.False(body.RootElement.GetProperty("authConfigured").GetBoolean());
        // This is an account prerequisite, not a wizard lock: creation remains reachable.
        Assert.True((await f.Request("POST", "/api/auth/create-admin")).Reached);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Administrator_PrivateHttpApiRequiresAuthenticationEvenOnLoopbackRegardlessOfOnboarding(bool onboardingDone)
    {
        using var f = new Fixture(onboardingDone);
        var (hash, salt) = CredentialValidator.HashPassword("Fixture-Password42!");
        Assert.True(f.Settings.TryCreateInitialAdministrator(new SecuritySettings
        {
            Username = "admin", PasswordHash = hash, PasswordSalt = salt
        }));
        var (anonymous, reached) = await f.Request("GET", "/api/sources");
        Assert.False(reached);
        Assert.Equal(401, anonymous.Response.StatusCode);
        var (authenticated, passed) = await f.Request("GET", "/api/sources", authenticated: true);
        Assert.True(passed);
        Assert.Equal(200, authenticated.Response.StatusCode);
        Assert.False(f.Settings.TryCreateInitialAdministrator(new SecuritySettings()));
    }

    [Theory]
    [InlineData("POST", "/health")]
    [InlineData("GET", "/health/deep")]
    [InlineData("GET", "/api/health")]
    [InlineData("POST", "/assets/main.js")]
    public async Task PublicExceptionsDoNotOpenPrivatePathsOrOtherMethods(string method, string path)
    {
        using var f = new Fixture();
        var (context, reached) = await f.Request(method, path);
        Assert.False(reached);
        Assert.Equal(403, context.Response.StatusCode);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "feedarr-setup-contract-" + Guid.NewGuid().ToString("N"));
        private readonly MemoryCache cache = new(new MemoryCacheOptions());
        public SettingsRepository Settings { get; }

        public Fixture(bool onboardingDone = false)
        {
            var db = new Db(OptionsFactory.Create(new AppOptions { DataDir = directory }));
            new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();
            Settings = new SettingsRepository(db);
            Settings.SaveUi(new UiSettings { OnboardingDone = onboardingDone });
        }

        public async Task<(HttpContext Context, bool Reached)> Request(string method, string path, bool authenticated = false)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Scheme = "http";
            context.Request.Path = path;
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Response.Body = new MemoryStream();
            if (authenticated)
                context.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:Fixture-Password42!"));
            var reached = false;
            var middleware = new BasicAuthMiddleware(_ => { reached = true; return Task.CompletedTask; }, new ConfigurationBuilder().Build());
            await middleware.InvokeAsync(context, Settings, cache,
                new AuthThrottleService(new BasicAuthThrottleOptions(), TimeProvider.System), NullLogger<BasicAuthMiddleware>.Instance);
            return (context, reached);
        }

        public void Dispose()
        {
            cache.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
