using System.Text.Json;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class SecuritySettingsValidationTests
{
    [Fact]
    public void SavingSmartExposedWithoutCreds_Returns400()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        var result = controller.PutSecurity(new SettingsController.SecuritySettingsDto
        {
            AuthMode = "smart",
            PublicBaseUrl = "https://example.com",
            Username = "",
            Password = "",
            PasswordConfirmation = ""
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var payload = SerializeToElement(bad.Value);
        Assert.Equal("credentials_required", payload.GetProperty("error").GetString());
    }

    [Fact]
    public void SavingOpenWithoutCreds_OK()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        var result = controller.PutSecurity(new SettingsController.SecuritySettingsDto
        {
            AuthMode = "open",
            PublicBaseUrl = "https://example.com",
            Username = "",
            Password = "",
            PasswordConfirmation = ""
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void SavingSmartWithForwardedHeadersAndNoCreds_Returns400()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController(headers: new Dictionary<string, string>
        {
            ["X-Forwarded-Host"] = "feedarr.example.com"
        });

        var result = controller.PutSecurity(new SettingsController.SecuritySettingsDto
        {
            AuthMode = "smart",
            PublicBaseUrl = "",
            Username = "",
            Password = "",
            PasswordConfirmation = ""
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var payload = SerializeToElement(bad.Value);
        Assert.Equal("credentials_required", payload.GetProperty("error").GetString());
    }

    [Fact]
    public void SavingSmartWithoutForwardedHeadersAndEmptyPublicBaseUrl_OK()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        var result = controller.PutSecurity(new SettingsController.SecuritySettingsDto
        {
            AuthMode = "smart",
            PublicBaseUrl = "",
            Username = "",
            Password = "",
            PasswordConfirmation = ""
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void SavingSmartLocalWithoutCreds_OK()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        var result = controller.PutSecurity(new SettingsController.SecuritySettingsDto
        {
            AuthMode = "smart",
            PublicBaseUrl = "http://localhost:6767",
            Username = "",
            Password = "",
            PasswordConfirmation = ""
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void SavingStrictWithoutCreds_Returns400()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        var result = controller.PutSecurity(new SettingsController.SecuritySettingsDto
        {
            AuthMode = "strict",
            PublicBaseUrl = "",
            Username = "",
            Password = "",
            PasswordConfirmation = ""
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var payload = SerializeToElement(bad.Value);
        Assert.Equal("credentials_required", payload.GetProperty("error").GetString());
    }

    [Fact]
    public void SavingStrictWithCreds_OK()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        var result = controller.PutSecurity(new SettingsController.SecuritySettingsDto
        {
            AuthMode = "strict",
            PublicBaseUrl = "",
            Username = "admin",
            Password = "StrongP@ssw0rd!",
            PasswordConfirmation = "StrongP@ssw0rd!"
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void SavingSmartExposedWithCreds_OK()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        var result = controller.PutSecurity(new SettingsController.SecuritySettingsDto
        {
            AuthMode = "smart",
            PublicBaseUrl = "https://example.com",
            Username = "admin",
            Password = "StrongP@ssw0rd!",
            PasswordConfirmation = "StrongP@ssw0rd!"
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    // -----------------------------------------------------------------------
    // Open mode downgrade guard (Scope 5)
    // -----------------------------------------------------------------------

    [Fact]
    public void DowngradeToOpen_WithoutConfirmation_WhenCredsExist_Returns400()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        // First: configure smart mode with credentials
        controller.PutSecurity(new SettingsController.SecuritySettingsDto
        {
            AuthMode = "smart",
            Username = "admin",
            Password = "StrongP@ssw0rd!",
            PasswordConfirmation = "StrongP@ssw0rd!"
        });

        // Attempt to downgrade to open without confirmation
        var result = controller.PutSecurity(new SettingsController.SecuritySettingsDto
        {
            AuthMode = "open",
            AllowDowngradeToOpen = null
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var payload = SerializeToElement(bad.Value);
        Assert.Equal("downgrade_confirmation_required", payload.GetProperty("error").GetString());
    }

    [Fact]
    public void DowngradeToOpen_WithConfirmation_WhenCredsExist_OK()
    {
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        // First: configure smart mode with credentials
        controller.PutSecurity(new SettingsController.SecuritySettingsDto
        {
            AuthMode = "smart",
            Username = "admin",
            Password = "StrongP@ssw0rd!",
            PasswordConfirmation = "StrongP@ssw0rd!"
        });

        // Downgrade with explicit confirmation
        var result = controller.PutSecurity(new SettingsController.SecuritySettingsDto
        {
            AuthMode = "open",
            AllowDowngradeToOpen = true
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = SerializeToElement(ok.Value);
        Assert.Equal("", payload.GetProperty("username").GetString());
        Assert.False(payload.GetProperty("hasPassword").GetBoolean());
        Assert.False(payload.GetProperty("authConfigured").GetBoolean());

        var reload = Assert.IsType<OkObjectResult>(controller.GetSecurity());
        var reloaded = SerializeToElement(reload.Value);
        Assert.Equal("", reloaded.GetProperty("username").GetString());
        Assert.False(reloaded.GetProperty("hasPassword").GetBoolean());
        Assert.False(reloaded.GetProperty("authConfigured").GetBoolean());
    }

    [Fact]
    public void DowngradeToOpen_WithoutCreds_NoConfirmationNeeded()
    {
        // Fresh install: no credentials set yet — no confirmation required
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        var result = controller.PutSecurity(new SettingsController.SecuritySettingsDto
        {
            AuthMode = "open"
        });

        // Should succeed: no existing credentials to protect
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void SaveOpenToOpen_NoConfirmationNeeded()
    {
        // Already open, saving open again: no confirmation required
        using var fixture = new ControllerFixture();
        var controller = fixture.CreateController();

        controller.PutSecurity(new SettingsController.SecuritySettingsDto { AuthMode = "open" });

        var result = controller.PutSecurity(new SettingsController.SecuritySettingsDto { AuthMode = "open" });
        Assert.IsType<OkObjectResult>(result);
    }

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

        public SettingsController CreateController(
            string host = "localhost",
            Dictionary<string, string>? headers = null)
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
            controller.HttpContext.Request.Host = new HostString(host);
            controller.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
            if (headers is not null)
            {
                foreach (var (key, value) in headers)
                    controller.HttpContext.Request.Headers[key] = value;
            }
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
