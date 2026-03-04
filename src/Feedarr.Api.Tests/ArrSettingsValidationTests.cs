using System.Text.Json;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class ArrSettingsValidationTests
{
    [Fact]
    public void GetArr_UsesDefaultsWhenNoPersistedValueExists()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        var ok = Assert.IsType<OkObjectResult>(controller.GetArr());
        var payload = SerializeToElement(ok.Value);

        Assert.Equal(60, GetPropertyInsensitive(payload, "arrSyncIntervalMinutes").GetInt32());
        Assert.True(GetPropertyInsensitive(payload, "arrAutoSyncEnabled").GetBoolean());
        Assert.Equal("arr", GetPropertyInsensitive(payload, "requestIntegrationMode").GetString());
    }

    [Fact]
    public void PutArr_InvalidValues_ReturnsValidationProblemDetails()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        var result = controller.PutArr(new ArrSettings
        {
            ArrSyncIntervalMinutes = 0,
            RequestIntegrationMode = "plex",
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var details = Assert.IsType<ValidationProblemDetails>(bad.Value);

        Assert.Contains("arrSyncIntervalMinutes", details.Errors.Keys);
        Assert.Contains("requestIntegrationMode", details.Errors.Keys);
    }

    [Fact]
    public void PutArr_RoundTripsAndUpdatesGeneralProjection()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        var put = controller.PutArr(new ArrSettings
        {
            ArrSyncIntervalMinutes = 15,
            ArrAutoSyncEnabled = false,
            RequestIntegrationMode = "jellyseerr",
        });

        var ok = Assert.IsType<OkObjectResult>(put);
        var payload = SerializeToElement(ok.Value);
        Assert.Equal(15, GetPropertyInsensitive(payload, "arrSyncIntervalMinutes").GetInt32());
        Assert.False(GetPropertyInsensitive(payload, "arrAutoSyncEnabled").GetBoolean());
        Assert.Equal("jellyseerr", GetPropertyInsensitive(payload, "requestIntegrationMode").GetString());

        var get = Assert.IsType<OkObjectResult>(controller.GetArr());
        var reloaded = SerializeToElement(get.Value);
        Assert.Equal(15, GetPropertyInsensitive(reloaded, "arrSyncIntervalMinutes").GetInt32());
        Assert.False(GetPropertyInsensitive(reloaded, "arrAutoSyncEnabled").GetBoolean());
        Assert.Equal("jellyseerr", GetPropertyInsensitive(reloaded, "requestIntegrationMode").GetString());

        var general = fixture.Settings.GetGeneral(new GeneralSettings());
        Assert.Equal(15, general.ArrSyncIntervalMinutes);
        Assert.False(general.ArrAutoSyncEnabled);
        Assert.Equal("jellyseerr", general.RequestIntegrationMode);
    }

    private static JsonElement SerializeToElement(object? value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static JsonElement GetPropertyInsensitive(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        throw new KeyNotFoundException(propertyName);
    }

    private sealed class ControllerFixture : IDisposable
    {
        private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
        private readonly IConfiguration _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        private readonly TestWorkspace _workspace = new();

        public ControllerFixture()
        {
            Db = CreateDb(_workspace);
            new MigrationsRunner(Db, NullLogger<MigrationsRunner>.Instance).Run();
            Settings = new SettingsRepository(Db);
        }

        public Db Db { get; }
        public SettingsRepository Settings { get; }

        public SettingsController CreateController(AppOptions? options = null)
        {
            var controller = new SettingsController(
                Settings,
                OptionsFactory.Create(options ?? new AppOptions()),
                null!,
                null!,
                null!,
                null!,
                _cache,
                _configuration,
                new Feedarr.Api.Services.Security.BootstrapTokenService(),
                NullLogger<SettingsController>.Instance,
                externalProviderInstances: null,
                db: Db);

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
            controller.HttpContext.Request.Host = new HostString("localhost");
            controller.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
            return controller;
        }

        public void Dispose()
        {
            _cache.Dispose();
            _workspace.Dispose();
        }
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
