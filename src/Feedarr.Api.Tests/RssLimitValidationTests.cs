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

public sealed class RssLimitValidationTests
{
    // Bounds
    private const int PerCatMin = 10, PerCatMax = 500;
    private const int GlobalMin = 50, GlobalMax = 2000;

    // -----------------------------------------------------------------------
    // RssLimitPerCategory bounds
    // -----------------------------------------------------------------------

    [Fact]
    public void PerCatBelowMin_ClampedTo10()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        var result = controller.PutGeneral(new GeneralSettings
        {
            RssLimitPerCategory = 1,
            RssLimitGlobalPerSource = 250
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = SerializeToElement(ok.Value);
        Assert.Equal(PerCatMin, payload.GetProperty("RssLimitPerCategory").GetInt32());
    }

    [Fact]
    public void PerCatAboveMax_ClampedTo500()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        var result = controller.PutGeneral(new GeneralSettings
        {
            RssLimitPerCategory = 9999,
            RssLimitGlobalPerSource = 9999
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = SerializeToElement(ok.Value);
        Assert.Equal(PerCatMax, payload.GetProperty("RssLimitPerCategory").GetInt32());
    }

    // -----------------------------------------------------------------------
    // RssLimitGlobalPerSource bounds
    // -----------------------------------------------------------------------

    [Fact]
    public void GlobalBelowMin_ClampedTo50()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        var result = controller.PutGeneral(new GeneralSettings
        {
            RssLimitPerCategory = 10,
            RssLimitGlobalPerSource = 5
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = SerializeToElement(ok.Value);
        // After clamping global to 50, it is >= perCat(10) so no further adjustment
        Assert.Equal(GlobalMin, payload.GetProperty("RssLimitGlobalPerSource").GetInt32());
    }

    [Fact]
    public void GlobalAboveMax_ClampedTo2000()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        var result = controller.PutGeneral(new GeneralSettings
        {
            RssLimitPerCategory = 50,
            RssLimitGlobalPerSource = 99999
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = SerializeToElement(ok.Value);
        Assert.Equal(GlobalMax, payload.GetProperty("RssLimitGlobalPerSource").GetInt32());
    }

    // -----------------------------------------------------------------------
    // Valid values — returned as-is
    // -----------------------------------------------------------------------

    [Fact]
    public void ValidValues_ReturnedAsIs()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        var result = controller.PutGeneral(new GeneralSettings
        {
            RssLimitPerCategory = 100,
            RssLimitGlobalPerSource = 400,
            SyncIntervalMinutes = 30
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = SerializeToElement(ok.Value);
        Assert.Equal(100, payload.GetProperty("RssLimitPerCategory").GetInt32());
        Assert.Equal(400, payload.GetProperty("RssLimitGlobalPerSource").GetInt32());
    }

    // -----------------------------------------------------------------------
    // Coherence: globalLimit >= perCatLimit
    // -----------------------------------------------------------------------

    [Fact]
    public void GlobalLessThanPerCat_ForcedToPerCat()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        var result = controller.PutGeneral(new GeneralSettings
        {
            RssLimitPerCategory = 300,
            RssLimitGlobalPerSource = 100
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = SerializeToElement(ok.Value);
        Assert.Equal(300, payload.GetProperty("RssLimitPerCategory").GetInt32());
        // global was 100 → clamped to GlobalMin(50) → still < perCat(300) → forced to 300
        Assert.Equal(300, payload.GetProperty("RssLimitGlobalPerSource").GetInt32());
    }

    [Fact]
    public void GlobalEqualToPerCat_Accepted()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        var result = controller.PutGeneral(new GeneralSettings
        {
            RssLimitPerCategory = 100,
            RssLimitGlobalPerSource = 100
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = SerializeToElement(ok.Value);
        Assert.Equal(100, payload.GetProperty("RssLimitPerCategory").GetInt32());
        Assert.Equal(100, payload.GetProperty("RssLimitGlobalPerSource").GetInt32());
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static JsonElement SerializeToElement(object? value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<JsonElement>(json);
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

        public SettingsController CreateController()
        {
            var controller = new SettingsController(
                Settings,
                OptionsFactory.Create(new AppOptions()),
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
            catch { }
        }
    }
}
