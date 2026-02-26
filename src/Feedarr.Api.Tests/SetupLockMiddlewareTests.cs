using System.IO;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class SetupLockMiddlewareTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/library")]
    public async Task WithoutSetup_BrowserRoutes_RedirectToSetup(string path)
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var settings = new SettingsRepository(db);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var setupState = new SetupStateService(settings, cache);

        var nextCalled = false;
        var middleware = new BasicAuthMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, BuildConfiguration());

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(
            context,
            settings,
            cache,
            setupState,
            NullLogger<BasicAuthMiddleware>.Instance);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("/setup", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task WithoutSetup_ApiRoute_Returns403WithSetupRequiredJson()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var settings = new SettingsRepository(db);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var setupState = new SetupStateService(settings, cache);

        var nextCalled = false;
        var middleware = new BasicAuthMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, BuildConfiguration());

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/something";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(
            context,
            settings,
            cache,
            setupState,
            NullLogger<BasicAuthMiddleware>.Instance);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.StartsWith("application/json", context.Response.ContentType ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        Assert.Contains("setup_required", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Setup required", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/setup")]
    [InlineData("/health")]
    [InlineData("/api/health")]
    [InlineData("/assets/main.js")]
    public async Task WithoutSetup_SetupAndHealthAndAssets_AreAccessible(string path)
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var settings = new SettingsRepository(db);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var setupState = new SetupStateService(settings, cache);

        var nextCalled = false;
        var middleware = new BasicAuthMiddleware(context =>
        {
            nextCalled = true;
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        }, BuildConfiguration());

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(
            context,
            settings,
            cache,
            setupState,
            NullLogger<BasicAuthMiddleware>.Instance);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task AfterSetup_GetRoot_IsAccessibleWithAuthNone()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var settings = new SettingsRepository(db);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var setupState = new SetupStateService(settings, cache);
        setupState.MarkSetupCompleted();

        var nextCalled = false;
        var middleware = new BasicAuthMiddleware(context =>
        {
            nextCalled = true;
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        }, BuildConfiguration());

        var context = new DefaultHttpContext();
        context.Request.Path = "/";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(
            context,
            settings,
            cache,
            setupState,
            NullLogger<BasicAuthMiddleware>.Instance);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
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

    private static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
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
