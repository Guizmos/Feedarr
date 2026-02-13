using System.Text.Json;
using Dapper;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Jackett;
using Feedarr.Api.Services.Prowlarr;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class SetupIndexerProvidersPersistenceTests
{
    [Fact]
    public void WizardProviderUpsert_PersistsJackettAndProwlarr_AndProvidersListReturnsBoth()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var protection = new PassthroughProtectionService();
        var settings = new SettingsRepository(db);
        var providers = new ProviderRepository(db, protection);
        var sources = new SourceRepository(db, protection);

        var setup = new SetupController(
            db,
            settings,
            providers,
            NullLogger<SetupController>.Instance);

        var first = setup.UpsertIndexerProvider("jackett", new SetupController.SetupIndexerProviderUpsertDto
        {
            BaseUrl = "http://localhost:9117",
            ApiKey = "jackett-key",
            Enabled = true
        });
        Assert.IsType<OkObjectResult>(first);

        var second = setup.UpsertIndexerProvider("prowlarr", new SetupController.SetupIndexerProviderUpsertDto
        {
            BaseUrl = "http://localhost:9696",
            ApiKey = "prowlarr-key",
            Enabled = true
        });
        Assert.IsType<OkObjectResult>(second);

        var listController = new ProvidersController(
            providers,
            sources,
            new JackettClient(new HttpClient()),
            new ProwlarrClient(new HttpClient()),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ProvidersController>.Instance);

        var listResult = Assert.IsType<OkObjectResult>(listController.List());
        var json = JsonSerializer.Serialize(listResult.Value);
        using var doc = JsonDocument.Parse(json);

        var types = doc.RootElement
            .EnumerateArray()
            .Select(el => (el.TryGetProperty("type", out var value) ? value.GetString() : "") ?? "")
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        Assert.Contains("jackett", types);
        Assert.Contains("prowlarr", types);

        using var conn = db.Open();
        var dbCount = conn.ExecuteScalar<long>("SELECT COUNT(1) FROM providers;");
        Assert.Equal(2, dbCount);
    }

    [Fact]
    public void WizardProviderUpsert_UpdatesOneTypeWithoutRemovingOther()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var protection = new PassthroughProtectionService();
        var settings = new SettingsRepository(db);
        var providers = new ProviderRepository(db, protection);

        var setup = new SetupController(
            db,
            settings,
            providers,
            NullLogger<SetupController>.Instance);

        setup.UpsertIndexerProvider("jackett", new SetupController.SetupIndexerProviderUpsertDto
        {
            BaseUrl = "http://localhost:9117",
            ApiKey = "jackett-key-v1",
            Enabled = true
        });
        setup.UpsertIndexerProvider("prowlarr", new SetupController.SetupIndexerProviderUpsertDto
        {
            BaseUrl = "http://localhost:9696",
            ApiKey = "prowlarr-key-v1",
            Enabled = true
        });
        setup.UpsertIndexerProvider("jackett", new SetupController.SetupIndexerProviderUpsertDto
        {
            BaseUrl = "http://localhost:9118",
            ApiKey = "jackett-key-v2",
            Enabled = true
        });

        using var conn = db.Open();
        var count = conn.ExecuteScalar<long>("SELECT COUNT(1) FROM providers;");
        Assert.Equal(2, count);

        var jackettBase = conn.ExecuteScalar<string>(
            "SELECT base_url FROM providers WHERE type = 'jackett' LIMIT 1;");
        var prowlarrBase = conn.ExecuteScalar<string>(
            "SELECT base_url FROM providers WHERE type = 'prowlarr' LIMIT 1;");

        Assert.Equal("http://localhost:9118", jackettBase);
        Assert.Equal("http://localhost:9696", prowlarrBase);
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

    private sealed class PassthroughProtectionService : IApiKeyProtectionService
    {
        public string Protect(string plainText) => plainText;
        public string Unprotect(string protectedText) => protectedText;
        public bool TryUnprotect(string protectedText, out string plainText)
        {
            plainText = protectedText;
            return true;
        }

        public bool IsProtected(string value) => false;
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
