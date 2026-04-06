using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Diagnostics;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

internal static class SystemStatsTestFactory
{
    public static Db CreateDb(StatsTestWorkspace workspace) =>
        new(OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        }));

    public static SystemStatsController CreateController(Db db, StatsTestWorkspace workspace)
    {
        var options = OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        });

        var settings = new SettingsRepository(db);
        var backup = new BackupService(
            db,
            new TestWebHostEnvironment(workspace.RootDir),
            options,
            settings,
            new BackupValidationService(),
            new BackupExecutionCoordinator(),
            new PassthroughProtectionService(),
            NullLogger<BackupService>.Instance);
        backup.InitializeForStartup();

        using var appLifetime = new TestHostApplicationLifetime();
        var storageCache = new StorageUsageCacheService(
            new MemoryCache(new MemoryCacheOptions()),
            new TestWebHostEnvironment(workspace.RootDir),
            options,
            db,
            appLifetime,
            NullLogger<StorageUsageCacheService>.Instance);

        var core = new SystemApiCore(
            db,
            new TestWebHostEnvironment(workspace.RootDir),
            settings,
            new ProviderStatsService(new StatsRepository(db, new MemoryCache(new MemoryCacheOptions()))),
            new ApiRequestMetricsService(),
            backup,
            new SystemStatusCacheService(
                new MemoryCache(new MemoryCacheOptions()),
                new SystemStatusSnapshotProvider(db, NullLogger<SystemStatusSnapshotProvider>.Instance),
                options,
                NullLogger<SystemStatusCacheService>.Instance),
            new MemoryCache(new MemoryCacheOptions()),
            new SetupStateService(settings, new MemoryCache(new MemoryCacheOptions())),
            storageCache,
            NullLogger<SystemApiCore>.Instance);

        return new SystemStatsController(core);
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
}

internal sealed class StatsTestWorkspace : IDisposable
{
    public StatsTestWorkspace()
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
