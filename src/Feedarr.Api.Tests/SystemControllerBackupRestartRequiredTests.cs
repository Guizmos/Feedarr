using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Diagnostics;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class SystemControllerBackupRestartRequiredTests
{
    [Fact]
    public void RestoreBackup_ReturnsConflict_WhenRestartIsRequired()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);

        var settings = new SettingsRepository(db);
        var stats = new ProviderStatsService(new StatsRepository(db, new MemoryCache(new MemoryCacheOptions())));
        var backupService = new BackupService(
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
            new PassthroughProtectionService(),
            NullLogger<BackupService>.Instance);

        backupService.InitializeForStartup();
        File.WriteAllText(Path.Combine(workspace.DataDir, "restore.restart-required.flag"), "required");

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

        var controller = new SystemController(
            db,
            new TestWebHostEnvironment(workspace.RootDir),
            settings,
            stats,
            new ApiRequestMetricsService(),
            backupService,
            new MemoryCache(new MemoryCacheOptions()),
            new SetupStateService(settings, new MemoryCache(new MemoryCacheOptions())),
            storageCache,
            NullLogger<SystemController>.Instance);

        var result = controller.RestoreBackup("backup.zip");
        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(409, conflict.StatusCode);
        Assert.Contains("Redemarrage requis", conflict.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
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
