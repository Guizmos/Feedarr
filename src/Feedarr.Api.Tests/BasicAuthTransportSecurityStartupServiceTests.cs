using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class BasicAuthTransportSecurityStartupServiceTests
{
    [Fact]
    public async Task Production_BasicAuthWithoutSecureTransport_FailsStartup()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var settings = new SettingsRepository(db);
        settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "strict",
            Username = "admin",
            PasswordHash = "hash",
            PasswordSalt = "salt"
        });

        var service = CreateService(
            environmentName: "Production",
            settings,
            new Dictionary<string, string?>
            {
                ["Security:RequireHttpsForBasicAuth"] = "true",
                ["Security:TrustedReverseProxyTls"] = "false",
                ["App:Security:EnforceHttps"] = "false"
            });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.Contains("secure transport", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Development_BasicAuthWithoutSecureTransport_AllowsStartup()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var settings = new SettingsRepository(db);
        settings.SaveSecurity(new SecuritySettings
        {
            AuthMode = "strict",
            Username = "admin",
            PasswordHash = "hash",
            PasswordSalt = "salt"
        });

        var service = CreateService(
            environmentName: "Development",
            settings,
            new Dictionary<string, string?>
            {
                ["Security:RequireHttpsForBasicAuth"] = "true",
                ["Security:TrustedReverseProxyTls"] = "false",
                ["App:Security:EnforceHttps"] = "false"
            });

        await service.StartAsync(CancellationToken.None);
    }

    private static BasicAuthTransportSecurityStartupService CreateService(
        string environmentName,
        SettingsRepository settingsRepository,
        Dictionary<string, string?> configValues)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        return new BasicAuthTransportSecurityStartupService(
            new TestHostEnvironment(environmentName),
            OptionsFactory.Create(new BasicAuthTransportSecurityOptions
            {
                RequireHttpsForBasicAuth = configuration.GetValue<bool?>("Security:RequireHttpsForBasicAuth"),
                TrustedReverseProxyTls = configuration.GetValue("Security:TrustedReverseProxyTls", false)
            }),
            OptionsFactory.Create(new ForwardedHeadersOptions()),
            configuration,
            settingsRepository,
            NullLogger<BasicAuthTransportSecurityStartupService>.Instance);
    }

    private static Db CreateDb(TestWorkspace workspace)
        => new(OptionsFactory.Create(new AppOptions { DataDir = workspace.DataDir, DbFileName = "feedarr.db" }));

    private sealed class TestHostEnvironment : IWebHostEnvironment
    {
        public TestHostEnvironment(string environmentName)
        {
            ApplicationName = "Feedarr.Api.Tests";
            EnvironmentName = environmentName;
            ContentRootPath = AppContext.BaseDirectory;
            ContentRootFileProvider = new NullFileProvider();
            WebRootPath = AppContext.BaseDirectory;
            WebRootFileProvider = new NullFileProvider();
        }

        public string ApplicationName { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public string EnvironmentName { get; set; }
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
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
