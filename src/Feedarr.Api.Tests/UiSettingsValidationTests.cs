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

public sealed class UiSettingsValidationTests
{
    [Fact]
    public void GetUi_UsesDefaultsWhenNoPersistedValueExists()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        var ok = Assert.IsType<OkObjectResult>(controller.GetUi());
        var payload = SerializeToElement(ok.Value);

        Assert.Equal("fr-FR", GetPropertyInsensitive(payload, "uiLanguage").GetString());
        Assert.Equal("fr-FR", GetPropertyInsensitive(payload, "mediaInfoLanguage").GetString());
        Assert.Equal("grid", GetPropertyInsensitive(payload, "defaultView").GetString());
        Assert.Equal("light", GetPropertyInsensitive(payload, "theme").GetString());
        Assert.Empty(GetPropertyInsensitive(payload, "sourceOptions").EnumerateArray());
        Assert.Empty(GetPropertyInsensitive(payload, "appOptions").EnumerateArray());
        Assert.Empty(GetPropertyInsensitive(payload, "categoryOptions").EnumerateArray());
    }

    [Fact]
    public void PutUi_ValidPayload_PersistsAndReturnsNormalizedSettings()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        var result = controller.PutUi(new UiSettings
        {
            UiLanguage = "en-US",
            MediaInfoLanguage = "fr-FR",
            HideSeenByDefault = true,
            ShowCategories = false,
            ShowTop24DedupeControl = true,
            EnableMissingPosterView = true,
            DefaultView = "poster",
            DefaultSort = "downloads",
            DefaultMaxAgeDays = "7",
            DefaultLimit = 200,
            DefaultFilterSeen = "0",
            DefaultFilterApplication = "__hide_apps__",
            DefaultFilterSourceId = "12",
            DefaultFilterCategoryId = "movie",
            DefaultFilterQuality = "1080p",
            BadgeInfo = true,
            BadgeWarn = false,
            BadgeError = true,
            Theme = "system",
            AnimationsEnabled = false,
            OnboardingDone = true,
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = SerializeToElement(ok.Value);
        Assert.Equal("en-US", GetPropertyInsensitive(payload, "uiLanguage").GetString());
        Assert.Equal("downloads", GetPropertyInsensitive(payload, "defaultSort").GetString());
        Assert.Equal("films", GetPropertyInsensitive(payload, "defaultFilterCategoryId").GetString());
        Assert.Equal("system", GetPropertyInsensitive(payload, "theme").GetString());
        Assert.True(GetPropertyInsensitive(payload, "enableMissingPosterView").GetBoolean());
        Assert.False(GetPropertyInsensitive(payload, "animationsEnabled").GetBoolean());

        var get = Assert.IsType<OkObjectResult>(controller.GetUi());
        var persisted = SerializeToElement(get.Value);
        Assert.Equal("poster", GetPropertyInsensitive(persisted, "defaultView").GetString());
        Assert.Equal("films", GetPropertyInsensitive(persisted, "defaultFilterCategoryId").GetString());
        Assert.Equal(200, GetPropertyInsensitive(persisted, "defaultLimit").GetInt32());
        Assert.True(GetPropertyInsensitive(persisted, "onboardingDone").GetBoolean());
    }

    [Fact]
    public void PutUi_InvalidValues_ReturnsValidationProblemDetails()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        var result = controller.PutUi(new UiSettings
        {
            UiLanguage = "es-ES",
            MediaInfoLanguage = "de-DE",
            DefaultView = "cards",
            DefaultSort = "score",
            DefaultMaxAgeDays = "99",
            DefaultLimit = 42,
            DefaultFilterSeen = "maybe",
            DefaultFilterApplication = "abc",
            DefaultFilterSourceId = "source",
            DefaultFilterCategoryId = "unknown",
            DefaultFilterQuality = new string('x', 65),
            Theme = "neon",
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var details = Assert.IsType<ValidationProblemDetails>(bad.Value);

        Assert.Contains("uiLanguage", details.Errors.Keys);
        Assert.Contains("mediaInfoLanguage", details.Errors.Keys);
        Assert.Contains("defaultView", details.Errors.Keys);
        Assert.Contains("defaultSort", details.Errors.Keys);
        Assert.Contains("defaultMaxAgeDays", details.Errors.Keys);
        Assert.Contains("defaultLimit", details.Errors.Keys);
        Assert.Contains("defaultFilterSeen", details.Errors.Keys);
        Assert.Contains("defaultFilterApplication", details.Errors.Keys);
        Assert.Contains("defaultFilterSourceId", details.Errors.Keys);
        Assert.Contains("defaultFilterCategoryId", details.Errors.Keys);
        Assert.Contains("defaultFilterQuality", details.Errors.Keys);
        Assert.Contains("theme", details.Errors.Keys);
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
