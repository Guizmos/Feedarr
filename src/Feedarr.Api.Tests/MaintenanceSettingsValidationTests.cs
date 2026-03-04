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

public sealed class MaintenanceSettingsValidationTests
{
    [Fact]
    public void PutMaintenance_ValidPayload_PersistsAndReturnsNormalizedSettings()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController(new AppOptions { SyncSourcesMaxConcurrency = 4 });

        var result = controller.PutMaintenance(new MaintenanceSettings
        {
            MaintenanceAdvancedOptionsEnabled = true,
            SyncSourcesMaxConcurrency = 3,
            PosterWorkers = 2,
            ProviderRateLimitMode = "manual",
            ProviderConcurrencyManual = new ProviderConcurrencyManualSettings
            {
                Tmdb = 3,
                Igdb = 2,
                Fanart = 2,
                Tvmaze = 2,
                Others = 2,
            },
            SyncRunTimeoutMinutes = 18,
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = SerializeToElement(ok.Value);
        Assert.True(GetPropertyInsensitive(payload, "maintenanceAdvancedOptionsEnabled").GetBoolean());
        Assert.Equal(3, GetPropertyInsensitive(payload, "syncSourcesMaxConcurrency").GetInt32());
        Assert.Equal(2, GetPropertyInsensitive(payload, "posterWorkers").GetInt32());
        Assert.Equal("manual", GetPropertyInsensitive(payload, "providerRateLimitMode").GetString());
        Assert.Equal(18, GetPropertyInsensitive(payload, "syncRunTimeoutMinutes").GetInt32());

        var get = Assert.IsType<OkObjectResult>(controller.GetMaintenance());
        var persisted = SerializeToElement(get.Value);
        Assert.True(GetPropertyInsensitive(persisted, "maintenanceAdvancedOptionsEnabled").GetBoolean());
        Assert.Equal(3, GetPropertyInsensitive(persisted, "syncSourcesMaxConcurrency").GetInt32());
        Assert.Equal(2, GetPropertyInsensitive(persisted, "posterWorkers").GetInt32());
        Assert.Equal(2, GetPropertyInsensitive(GetPropertyInsensitive(persisted, "providerConcurrencyManual"), "igdb").GetInt32());
    }

    [Fact]
    public void PutMaintenance_InvalidValues_ReturnsValidationProblemDetails()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        var result = controller.PutMaintenance(new MaintenanceSettings
        {
            SyncSourcesMaxConcurrency = 0,
            PosterWorkers = 3,
            ProviderRateLimitMode = "burst",
            ProviderConcurrencyManual = new ProviderConcurrencyManualSettings
            {
                Tmdb = 4,
                Igdb = 0,
                Fanart = 3,
                Tvmaze = 0,
                Others = 3,
            },
            SyncRunTimeoutMinutes = 2,
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var details = Assert.IsType<ValidationProblemDetails>(bad.Value);

        Assert.Contains("syncSourcesMaxConcurrency", details.Errors.Keys);
        Assert.Contains("posterWorkers", details.Errors.Keys);
        Assert.Contains("providerRateLimitMode", details.Errors.Keys);
        Assert.Contains("providerConcurrencyManual.tmdb", details.Errors.Keys);
        Assert.Contains("providerConcurrencyManual.igdb", details.Errors.Keys);
        Assert.Contains("syncRunTimeoutMinutes", details.Errors.Keys);
    }

    [Fact]
    public void GetMaintenance_UsesAppDefaultWhenNoPersistedValueExists()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController(new AppOptions { SyncSourcesMaxConcurrency = 4 });

        var ok = Assert.IsType<OkObjectResult>(controller.GetMaintenance());
        var payload = SerializeToElement(ok.Value);

        Assert.Equal(4, GetPropertyInsensitive(payload, "syncSourcesMaxConcurrency").GetInt32());
        Assert.Equal(1, GetPropertyInsensitive(payload, "posterWorkers").GetInt32());
        Assert.Equal("auto", GetPropertyInsensitive(payload, "providerRateLimitMode").GetString());
        Assert.Empty(GetPropertyInsensitive(payload, "configuredProviders").EnumerateArray());
    }

    [Fact]
    public void GetMaintenance_IncludesConfiguredProvidersFromLegacyExternalSettings()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        fixture.Settings.SaveExternalPartial(new ExternalSettings
        {
            TmdbApiKey = "tmdb-key",
            IgdbClientId = "igdb-id",
            IgdbClientSecret = "igdb-secret",
            FanartEnabled = false,
            TvmazeEnabled = false,
        });

        var ok = Assert.IsType<OkObjectResult>(controller.GetMaintenance());
        var payload = SerializeToElement(ok.Value);
        var providers = GetPropertyInsensitive(payload, "configuredProviders")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        Assert.Equal(["igdb", "tmdb"], providers);
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
            var db = CreateDb(_workspace);
            new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();
            Settings = new SettingsRepository(db);
        }

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
                NullLogger<SettingsController>.Instance);

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
