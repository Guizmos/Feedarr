using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Dtos.Providers;
using Feedarr.Api.Filters;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Arr;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Diagnostics;
using Feedarr.Api.Services.Jackett;
using Feedarr.Api.Services.Prowlarr;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class CriticalFlowsAndPerfTests
{
    [Fact]
    public void ApiRequestMetricsService_ComputesP95AndErrorRate()
    {
        var metrics = new ApiRequestMetricsService();

        for (var i = 1; i <= 10; i++)
            metrics.Record("GET", "api/system/stats", i == 10 ? 500 : 200, i);

        var snapshot = metrics.Snapshot(top: 5);
        Assert.Equal(1, snapshot.EndpointCount);
        Assert.Equal(10, snapshot.TotalRequests);
        Assert.Equal(1, snapshot.TotalErrors);
        Assert.Equal(10.0, snapshot.ErrorRatePercent);

        var endpoint = Assert.Single(snapshot.Endpoints);
        Assert.Equal("GET", endpoint.Method);
        Assert.Equal("api/system/stats", endpoint.Route);
        Assert.Equal(10, endpoint.WindowCount);
        Assert.Equal(5, endpoint.P50Ms);
        Assert.Equal(10, endpoint.P95Ms);
        Assert.Equal(10, endpoint.P99Ms);
    }

    [Fact]
    public async Task ArrLibrarySyncService_SyncAppAsync_PersistsSonarrLibraryItems()
    {
        using var workspace = new TestWorkspace();
        var appOptions = OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        });

        var services = new ServiceCollection();
        services.AddSingleton<IOptions<AppOptions>>(appOptions);
        services.AddSingleton<Db>();
        services.AddSingleton<IApiKeyProtectionService, PassthroughProtectionService>();
        services.AddSingleton<ArrApplicationRepository>();
        services.AddSingleton<ArrLibraryRepository>();
        services.AddSingleton(new SonarrClient(new HttpClient(new SonarrSeriesHandler())));
        services.AddSingleton(new RadarrClient(new HttpClient(new EmptyJsonHandler())));
        services.AddSingleton(new EerrRequestClient(new HttpClient(new EmptyJsonHandler())));
        services.AddSingleton<BackupExecutionCoordinator>();
        services.AddSingleton<ArrLibrarySyncService>();
        services.AddLogging();

        using var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<Db>();
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var apps = provider.GetRequiredService<ArrApplicationRepository>();
        var appId = apps.Create(
            "sonarr",
            "Test Sonarr",
            "http://localhost:8989",
            "secret",
            null,
            null,
            null,
            null,
            seasonFolder: true,
            monitorMode: null,
            searchMissing: true,
            searchCutoff: false,
            minimumAvailability: null,
            searchForMovie: true);

        var syncService = provider.GetRequiredService<ArrLibrarySyncService>();
        await syncService.SyncAppAsync(appId, CancellationToken.None);

        using var conn = db.Open();
        var count = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM arr_library_items WHERE app_id = @id", new { id = appId });
        var syncCount = conn.ExecuteScalar<int>("SELECT COALESCE(last_sync_count, 0) FROM arr_sync_status WHERE app_id = @id", new { id = appId });

        Assert.Equal(1, count);
        Assert.Equal(1, syncCount);
    }

    [Fact]
    public void SetupAndSystem_OnboardingFlow_CompletesAndPersistsProvider()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var protection = new PassthroughProtectionService();
        var settings = new SettingsRepository(db);
        var providers = new ProviderRepository(db, protection);

        using var setupCache = new MemoryCache(new MemoryCacheOptions());
        var setup = new SetupController(
            db,
            settings,
            providers,
            BuildConfiguration(),
            new BootstrapTokenService(),
            new SetupStateService(settings, setupCache),
            NullLogger<SetupController>.Instance);

        var upsert = setup.UpsertIndexerProvider("jackett", new SetupController.SetupIndexerProviderUpsertDto
        {
            BaseUrl = "http://localhost:9117",
            ApiKey = "jackett-key",
            Enabled = true
        });
        Assert.IsType<OkObjectResult>(upsert);

        var backup = new BackupService(
            db,
            new TestWebHostEnvironment(workspace.RootDir),
            OptionsFactory.Create(new AppOptions
            {
                DataDir = workspace.DataDir,
                DbFileName = "feedarr.db"
            }),
            settings,
            new BackupValidationService(),
            new BackupExecutionCoordinator(),
            protection,
            NullLogger<BackupService>.Instance);
        backup.InitializeForStartup();

        var appOptions = OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        });
        var storageCache = new StorageUsageCacheService(
            new MemoryCache(new MemoryCacheOptions()),
            new TestWebHostEnvironment(workspace.RootDir),
            appOptions,
            db,
            NullLogger<StorageUsageCacheService>.Instance);

        var system = new SystemController(
            db,
            new TestWebHostEnvironment(workspace.RootDir),
            settings,
            new ProviderStatsService(new StatsRepository(db, new MemoryCache(new MemoryCacheOptions()))),
            new ApiRequestMetricsService(),
            backup,
            new MemoryCache(new MemoryCacheOptions()),
            new SetupStateService(settings, new MemoryCache(new MemoryCacheOptions())),
            storageCache,
            NullLogger<SystemController>.Instance);

        var before = Assert.IsType<OkObjectResult>(system.Onboarding());
        using (var beforeDoc = JsonDocument.Parse(JsonSerializer.Serialize(before.Value)))
        {
            Assert.False(beforeDoc.RootElement.GetProperty("onboardingDone").GetBoolean());
        }

        var done = Assert.IsType<OkObjectResult>(system.CompleteOnboarding());
        using (var doneDoc = JsonDocument.Parse(JsonSerializer.Serialize(done.Value)))
        {
            Assert.True(doneDoc.RootElement.GetProperty("onboardingDone").GetBoolean());
        }

        var after = Assert.IsType<OkObjectResult>(system.Onboarding());
        using (var afterDoc = JsonDocument.Parse(JsonSerializer.Serialize(after.Value)))
        {
            Assert.True(afterDoc.RootElement.GetProperty("onboardingDone").GetBoolean());
        }

        using var conn = db.Open();
        var providerCount = conn.ExecuteScalar<long>("SELECT COUNT(1) FROM providers;");
        Assert.Equal(1, providerCount);
    }

    [Fact]
    public async Task ProvidersController_TestInline_WorksForJackettAndProwlarr()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var protection = new PassthroughProtectionService();
        var providers = new ProviderRepository(db, protection);
        var sources = new SourceRepository(db, protection);

        var controller = new ProvidersController(
            providers,
            sources,
            new JackettClient(new HttpClient(new JackettIndexersHandler())),
            new ProwlarrClient(new HttpClient(new ProwlarrIndexersHandler())),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ProvidersController>.Instance);

        var jackettResult = await controller.TestInline(new ProviderTestRequestDto
        {
            Type = "jackett",
            BaseUrl = "http://localhost:9117",
            ApiKey = "abc"
        }, CancellationToken.None);
        var jackettOk = Assert.IsType<OkObjectResult>(jackettResult);
        using (var jackettDoc = JsonDocument.Parse(JsonSerializer.Serialize(jackettOk.Value)))
        {
            Assert.True(jackettDoc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(1, jackettDoc.RootElement.GetProperty("count").GetInt32());
        }

        var prowlarrResult = await controller.TestInline(new ProviderTestRequestDto
        {
            Type = "prowlarr",
            BaseUrl = "http://localhost:9696",
            ApiKey = "xyz"
        }, CancellationToken.None);
        var prowlarrOk = Assert.IsType<OkObjectResult>(prowlarrResult);
        using (var prowlarrDoc = JsonDocument.Parse(JsonSerializer.Serialize(prowlarrOk.Value)))
        {
            Assert.True(prowlarrDoc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(1, prowlarrDoc.RootElement.GetProperty("count").GetInt32());
        }
    }

    [Fact]
    public async Task ApiErrorNormalizationFilter_NormalizesLegacyErrorShapeToProblemDetails()
    {
        var filter = new ApiErrorNormalizationFilter();
        var httpContext = new DefaultHttpContext();
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filters = new List<IFilterMetadata>();
        var original = new BadRequestObjectResult(new { error = "invalid scope", id = 7L });
        var markerController = new object();
        var executing = new ResultExecutingContext(actionContext, filters, original, markerController);

        await filter.OnResultExecutionAsync(executing, () =>
        {
            var executed = new ResultExecutedContext(actionContext, filters, executing.Result, markerController);
            return Task.FromResult(executed);
        });

        var normalized = Assert.IsType<ObjectResult>(executing.Result);
        Assert.Equal(400, normalized.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(normalized.Value);
        Assert.Equal("invalid scope", problem.Title);
        Assert.Equal(7L, problem.Extensions["id"]);
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

    private sealed class SonarrSeriesHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/api/v3/series", StringComparison.OrdinalIgnoreCase) == true)
            {
                var payload = """
                    [
                      {
                        "id": 101,
                        "tvdbId": 9991,
                        "title": "Integration Test Show",
                        "titleSlug": "integration-test-show",
                        "alternateTitles": []
                      }
                    ]
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class EmptyJsonHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class JackettIndexersHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/api/v2.0/indexers", StringComparison.OrdinalIgnoreCase) == true)
            {
                var payload = """
                    [
                      { "id": "indexer-a", "name": "Indexer A", "configured": true }
                    ]
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class ProwlarrIndexersHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/api/v1/indexer", StringComparison.OrdinalIgnoreCase) == true)
            {
                var payload = """
                    [
                      { "id": 42, "name": "Prowlarr A", "enable": true, "protocol": "torrent" }
                    ]
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
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

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public TestWebHostEnvironment(string rootDir)
        {
            ApplicationName = "Feedarr.Api.Tests";
            EnvironmentName = "Test";
            ContentRootPath = rootDir;
            ContentRootFileProvider = new NullFileProvider();
            WebRootPath = rootDir;
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
