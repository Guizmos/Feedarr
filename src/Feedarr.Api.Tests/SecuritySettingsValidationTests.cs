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
    public void DefaultPolicyRequiresAnAccountAndAllowsBothTransports()
    {
        using var fixture=new ControllerFixture();
        var payload=SerializeToElement(Assert.IsType<OkObjectResult>(fixture.CreateController().GetSecurity()).Value);
        Assert.True(payload.GetProperty("authRequired").GetBoolean()); Assert.False(payload.GetProperty("requireHttps").GetBoolean());
        Assert.False(payload.TryGetProperty("authMode",out _));
    }

    [Fact]
    public void AnonymousModeCannotBeEnabledEvenWithOldConfirmation()
    {
        using var fixture=new ControllerFixture();
        var result=Assert.IsType<ObjectResult>(fixture.CreateController().PutSecurity(new(){AuthMode="open",AllowDowngradeToOpen=true}));
        Assert.Equal(400,result.StatusCode);
    }

    [Fact]
    public void HttpsCannotBeRequiredFromAnHttpRequest()
    {
        using var fixture=new ControllerFixture();
        var (hash,salt)=Feedarr.Api.Services.Security.CredentialValidator.HashPassword("Fixture-Password42!");
        fixture.Settings.SaveSecurity(new(){Username="admin",PasswordHash=hash,PasswordSalt=salt});
        var result=Assert.IsType<ObjectResult>(fixture.CreateController().PutSecurity(new(){RequireHttps=true}));
        Assert.Equal(400,result.StatusCode);
        Assert.False(fixture.Settings.GetSecurity(new()).RequireHttps);
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
