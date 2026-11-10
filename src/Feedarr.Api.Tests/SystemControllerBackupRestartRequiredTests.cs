using System.IO.Compression;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
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
    public async Task RestoreEndpoint_RejectsLegacyRestart_ThenAcceptsPreparationIndependentOfRequest()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);

        var settings = new SettingsRepository(db);
        var stats = new ProviderStatsService(new StatsRepository(db, new MemoryCache(new MemoryCacheOptions())));
        var pendingStore = new PendingRestoreStore(workspace.DataDir, "feedarr.db");
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
            NullLogger<BackupService>.Instance, pendingStore);

        backupService.InitializeForStartup();
        File.WriteAllText(Path.Combine(workspace.DataDir, "restore.restart-required.flag"), "required");

        var appOptions = OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        });
        using var appLifetime = new TestHostApplicationLifetime();
        var storageCache = new StorageUsageCacheService(
            new MemoryCache(new MemoryCacheOptions()),
            new TestWebHostEnvironment(workspace.RootDir),
            appOptions,
            db,
            appLifetime,
            NullLogger<StorageUsageCacheService>.Instance);

        var core = new SystemApiCore(
            db,
            new TestWebHostEnvironment(workspace.RootDir),
            settings,
            stats,
            new ApiRequestMetricsService(),
            backupService,
            new SystemStatusCacheService(
                new MemoryCache(new MemoryCacheOptions()),
                new SystemStatusSnapshotProvider(db, NullLogger<SystemStatusSnapshotProvider>.Instance),
                appOptions,
                NullLogger<SystemStatusCacheService>.Instance),
            new MemoryCache(new MemoryCacheOptions()),
            new SetupStateService(settings, new MemoryCache(new MemoryCacheOptions())),
            storageCache,
            NullLogger<SystemApiCore>.Instance);
        var controller = new SystemBackupsController(core);

        var result = await controller.RestoreBackup("backup.zip");
        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(409, conflict.StatusCode);
        Assert.Contains("Redemarrage requis", conflict.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);

        // Same real controller: acceptance must finish before the heavy background copy.
        File.Delete(Path.Combine(workspace.DataDir, "restore.restart-required.flag"));
        var incoming = Path.Combine(workspace.RootDir, "incoming.db");
        using (var connection = new SqliteConnection($"Data Source={incoming};Pooling=False"))
        {
            connection.Open();
            connection.Execute("CREATE TABLE marker(value TEXT); INSERT INTO marker VALUES ('selected');");
        }
        Directory.CreateDirectory(Path.Combine(workspace.DataDir, "backups"));
        using (var zip = ZipFile.Open(Path.Combine(workspace.DataDir, "backups", "accepted.zip"), ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(incoming, "feedarr.db");
            using var info = zip.CreateEntry("info.json").Open();
            JsonSerializer.Serialize(info, new { version = "1.0.0", backupFormatVersion = 1, dbSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(incoming))) });
        }
        using var copying = new ManualResetEventSlim();
        using var releaseCopy = new ManualResetEventSlim();
        pendingStore.FaultPoint = phase => { if (phase == "copying-posters") { copying.Set(); Assert.True(releaseCopy.Wait(TimeSpan.FromSeconds(10))); } };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = workspace.RootDir, EnvironmentName = "Test" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(core);
        builder.Services.AddControllers().AddApplicationPart(typeof(SystemBackupsController).Assembly)
            .ConfigureApplicationPartManager(parts => parts.FeatureProviders.Add(new OnlyBackupController()));
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var request = new CancellationTokenSource();
        string? id;
        try
        {
            using var response = await http.PostAsync("/api/system/backups/accepted.zip/restore?confirm=true&background=true", null, request.Token).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal("/api/system/backups/state", response.Headers.Location?.OriginalString);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(payload.RootElement.GetProperty("accepted").GetBoolean());
            id = payload.RootElement.GetProperty("preparationId").GetString();
            Assert.True(copying.Wait(TimeSpan.FromSeconds(5)));
            request.Cancel();
            using var stateResponse = await http.GetAsync("/api/system/backups/state");
            stateResponse.EnsureSuccessStatusCode();
            using var snapshot = JsonDocument.Parse(await stateResponse.Content.ReadAsStringAsync());
            Assert.Equal("running", snapshot.RootElement.GetProperty("preparation").GetProperty("state").GetString());
            Assert.False(snapshot.RootElement.GetProperty("restorePending").GetProperty("pending").GetBoolean());
        }
        finally { releaseCopy.Set(); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (backupService.GetOperationState().Preparation?.State == "running") await Task.Delay(10, deadline.Token);
        Assert.Equal("completed", backupService.GetOperationState().Preparation?.State);
        Assert.Equal(id, backupService.GetPendingRestore().Id);
        var status = Assert.IsType<BackupOperationStateDto>(Assert.IsType<OkObjectResult>(controller.BackupState()).Value);
        Assert.Equal(id, status.RestorePending?.Id);
        using var pendingResponse = await http.GetAsync("/api/system/backups/restore-state");
        pendingResponse.EnsureSuccessStatusCode();
        using var manifestStatus = JsonDocument.Parse(await pendingResponse.Content.ReadAsStringAsync());
        Assert.Equal(id, manifestStatus.RootElement.GetProperty("id").GetString());
        pendingStore.FaultPoint = null;
        // Older clients still receive their historical HTTP 200 completion contract.
        Assert.IsType<OkObjectResult>(await controller.RestoreBackup("accepted.zip", true, replacePendingId: id));
        await app.StopAsync();
        await backupService.StopAsync(default);
    }

    private sealed class OnlyBackupController : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            foreach (var controller in feature.Controllers.Where(type => type.AsType() != typeof(SystemBackupsController)).ToArray())
                feature.Controllers.Remove(controller);
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
