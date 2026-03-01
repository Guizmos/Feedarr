using Dapper;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Titles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class MaintenanceControllerLockTests
{
    [Fact]
    public void ReparseTitles_WhenAnotherMaintenanceIsRunning_ReturnsConflict()
    {
        using var context = new MaintenanceControllerTestContext();
        using var heldLock = new MaintenanceLockHandle(context.LockService);
        var controller = context.CreateController();

        var result = controller.ReparseTitles();

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(409, conflict.StatusCode);
    }

    private sealed class MaintenanceControllerTestContext : IDisposable
    {
        private readonly TestWorkspace _workspace;
        private readonly TestWebHostEnvironment _environment;

        public MaintenanceControllerTestContext()
        {
            _workspace = new TestWorkspace();
            _environment = new TestWebHostEnvironment(_workspace.RootDir);
            Options = OptionsFactory.Create(new AppOptions
            {
                DataDir = _workspace.DataDir,
                DbFileName = "feedarr.db"
            });

            Db = new Db(Options);
            new MigrationsRunner(Db, NullLogger<MigrationsRunner>.Instance).Run();

            var protection = new PassthroughProtectionService();
            Settings = new SettingsRepository(Db, protection, NullLogger<SettingsRepository>.Instance);
            Releases = new ReleaseRepository(Db, new TitleParser(), new UnifiedCategoryResolver());
            Activity = new ActivityRepository(Db, new BadgeSignal());
            LockService = new MaintenanceLockService();

            var registry = new ExternalProviderRegistry();
            var instances = new ExternalProviderInstanceRepository(
                Db,
                Settings,
                protection,
                registry,
                NullLogger<ExternalProviderInstanceRepository>.Instance);
            var resolver = new ActiveExternalProviderConfigResolver(
                instances,
                registry,
                NullLogger<ActiveExternalProviderConfigResolver>.Instance);

            PosterFetch = new PosterFetchService(
                Releases,
                Activity,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                new PosterMatchCacheService(Db),
                Options,
                _environment,
                new PosterMatchingOrchestrator(
                    new VideoMatchingStrategy(),
                    new GameMatchingStrategy(),
                    new AnimeMatchingStrategy(),
                    new AudioMatchingStrategy(),
                    new GenericMatchingStrategy()),
                resolver,
                NullLogger<PosterFetchService>.Instance);

            using var conn = Db.Open();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            conn.Execute(
                """
                INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
                VALUES('test', 1, 'http://localhost:9117/api', 'key', 'query', @ts, @ts);
                INSERT INTO releases(source_id, guid, title, created_at_ts, published_at_ts, unified_category)
                VALUES(1, 'guid-1', 'Example.S01E01.1080p', @ts, @ts, 'Serie');
                """,
                new { ts });
        }

        public Db Db { get; }
        public ReleaseRepository Releases { get; }
        public ActivityRepository Activity { get; }
        public SettingsRepository Settings { get; }
        public PosterFetchService PosterFetch { get; }
        public MaintenanceLockService LockService { get; }
        public Microsoft.Extensions.Options.IOptions<AppOptions> Options { get; }

        public MaintenanceController CreateController()
        {
            return new MaintenanceController(
                Db,
                Releases,
                Activity,
                Settings,
                PosterFetch,
                new RetroFetchLogService(Options, _environment),
                null!,
                null!,
                null!,
                null!,
                Options,
                _environment,
                LockService,
                NullLogger<MaintenanceController>.Instance);
        }

        public void Dispose()
        {
            _workspace.Dispose();
        }
    }

    private sealed class MaintenanceLockHandle : IDisposable
    {
        private readonly MaintenanceLockService _lockService;

        public MaintenanceLockHandle(MaintenanceLockService lockService)
        {
            _lockService = lockService;
            Assert.True(_lockService.TryEnter());
        }

        public void Dispose()
        {
            _lockService.Release();
        }
    }

    private sealed class PassthroughProtectionService : IApiKeyProtectionService
    {
        public string Protect(string plainText) => plainText;
        public string Unprotect(string protectedText) => protectedText;
        public bool TryUnprotect(string protectedText, out string? plainText)
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
