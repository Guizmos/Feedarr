using System.IO.Compression;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class BackupServiceTests
{
    [Fact]
    public async Task CreateBackup_RedactsExternalSecretsInConfig()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        EnsureSettingsTable(db);

        var settings = new SettingsRepository(db);
        settings.SaveExternalPartial(new ExternalSettings
        {
            TmdbApiKey = "tmdb-secret",
            TvmazeApiKey = "tvmaze-secret",
            FanartApiKey = "fanart-secret",
            IgdbClientId = "igdb-id",
            IgdbClientSecret = "igdb-secret",
            TmdbEnabled = true,
            TvmazeEnabled = true,
            FanartEnabled = true,
            IgdbEnabled = true
        });

        var service = CreateBackupService(workspace, db, settings, new FakeApiKeyProtectionService());
        var backup = await service.CreateBackupAsync("1.2.3");
        var backupPath = Path.Combine(workspace.BackupsDir, backup.Name);

        using var archive = ZipFile.OpenRead(backupPath);
        var configEntry = archive.GetEntry("config.json");
        Assert.NotNull(configEntry);

        using var stream = configEntry!.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var external = root.GetProperty("external");

        Assert.True(external.GetProperty("hasTmdbApiKey").GetBoolean());
        Assert.True(external.GetProperty("hasTvmazeApiKey").GetBoolean());
        Assert.True(external.GetProperty("hasFanartApiKey").GetBoolean());
        Assert.True(external.GetProperty("hasIgdbClientId").GetBoolean());
        Assert.True(external.GetProperty("hasIgdbClientSecret").GetBoolean());

        Assert.False(external.TryGetProperty("tmdbApiKey", out _));
        Assert.False(external.TryGetProperty("tvmazeApiKey", out _));
        Assert.False(external.TryGetProperty("fanartApiKey", out _));
        Assert.False(external.TryGetProperty("igdbClientId", out _));
        Assert.False(external.TryGetProperty("igdbClientSecret", out _));
    }

    [Fact]
    public async Task RestoreBackup_ReencryptsPlaintextAndClearsUndecryptableCredentials()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);

        var settings = new SettingsRepository(db);
        var protection = new FakeApiKeyProtectionService();
        var service = CreateBackupService(workspace, db, settings, protection);

        var incomingDbPath = Path.Combine(workspace.RootDir, "incoming.db");
        CreateIncomingBackupDatabase(incomingDbPath);
        var checksum = ComputeFileSha256(incomingDbPath);

        var backupName = "restore_case.zip";
        var backupPath = Path.Combine(workspace.BackupsDir, backupName);
        Directory.CreateDirectory(workspace.BackupsDir);

        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(incomingDbPath, "feedarr.db");
            AddJsonEntry(archive, "info.json", new
            {
                version = "1.0.0",
                backupFormatVersion = 1,
                dbSha256 = checksum
            });
        }

        var result = await service.RestoreBackupAsync(backupName, "1.0.0");
        Assert.Equal(2, result.ReencryptedCredentials);
        Assert.Equal(1, result.ClearedUndecryptableCredentials);
        Assert.True(service.GetPendingRestore().Pending);
        // Simulate the new process startup; preparation alone never installs the DB.
        new PendingRestoreStore(workspace.DataDir, "feedarr.db").ApplyAtStartup();
        Assert.Equal("applied", service.GetPendingRestore().State);

        using var conn = db.Open();
        var sourceApiKey = conn.ExecuteScalar<string>("SELECT api_key FROM sources LIMIT 1;");
        var providerApiKey = conn.ExecuteScalar<string>("SELECT api_key FROM providers LIMIT 1;");
        var arrApiKey = conn.ExecuteScalar<string>("SELECT api_key_encrypted FROM arr_applications LIMIT 1;");

        Assert.StartsWith("ENC:PROT:", sourceApiKey);
        Assert.Equal(string.Empty, providerApiKey);
        Assert.StartsWith("ENC:PROT:", arrApiKey);
    }

    [Fact]
    public async Task RestoreBackup_RejectsChecksumMismatch()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        var settings = new SettingsRepository(db);
        var service = CreateBackupService(workspace, db, settings, new FakeApiKeyProtectionService());

        var incomingDbPath = Path.Combine(workspace.RootDir, "incoming_checksum.db");
        CreateIncomingBackupDatabase(incomingDbPath);

        var backupName = "restore_bad_checksum.zip";
        var backupPath = Path.Combine(workspace.BackupsDir, backupName);

        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(incomingDbPath, "feedarr.db");
            AddJsonEntry(archive, "info.json", new
            {
                version = "1.0.0",
                backupFormatVersion = 1,
                dbSha256 = new string('a', 64)
            });
        }

        var ex = await Assert.ThrowsAsync<BackupOperationException>(() => service.RestoreBackupAsync(backupName, "1.0.0"));
        Assert.Equal(StatusCodes.Status400BadRequest, ex.StatusCode);
        Assert.Contains("checksum mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestoreBackup_RejectsMissingChecksumMetadata()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        var settings = new SettingsRepository(db);
        var service = CreateBackupService(workspace, db, settings, new FakeApiKeyProtectionService());

        var incomingDbPath = Path.Combine(workspace.RootDir, "incoming_no_checksum.db");
        CreateIncomingBackupDatabase(incomingDbPath);

        var backupName = "restore_missing_checksum.zip";
        var backupPath = Path.Combine(workspace.BackupsDir, backupName);

        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(incomingDbPath, "feedarr.db");
            AddJsonEntry(archive, "info.json", new
            {
                version = "1.0.0",
                backupFormatVersion = 1
            });
        }

        var ex = await Assert.ThrowsAsync<BackupOperationException>(() => service.RestoreBackupAsync(backupName, "1.0.0"));
        Assert.Equal(StatusCodes.Status400BadRequest, ex.StatusCode);
        Assert.Contains("dbSha256", ex.Message, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task InvalidReplacementArchive_PreservesTheAlreadyPreparedRestore()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        var service = CreateBackupService(workspace, db, new SettingsRepository(db), new FakeApiKeyProtectionService());
        var incoming = Path.Combine(workspace.RootDir, "incoming.db");
        CreateIncomingBackupDatabase(incoming);
        Directory.CreateDirectory(workspace.BackupsDir);
        void Archive(string name, string checksum)
        {
            using var zip = ZipFile.Open(Path.Combine(workspace.BackupsDir, name), ZipArchiveMode.Create);
            zip.CreateEntryFromFile(incoming, "feedarr.db");
            AddJsonEntry(zip, "info.json", new { version = "1.0.0", backupFormatVersion = 1, dbSha256 = checksum });
        }
        Archive("valid.zip", ComputeFileSha256(incoming));
        await service.RestoreBackupAsync("valid.zip", "1.0.0");
        var pending = service.GetPendingRestore();
        Archive("invalid.zip", new string('0', 64));
        await Assert.ThrowsAsync<BackupOperationException>(() => service.RestoreBackupAsync("invalid.zip", "1.0.0", replacePendingId: pending.Id));
        Assert.Equal(pending.Id, service.GetPendingRestore().Id);
        Assert.DoesNotContain(workspace.RootDir, JsonSerializer.Serialize(pending));
    }

    [Fact]
    public async Task SlowPreparation_ReturnsBeforeCopy_AndSurvivesRequestCancellation()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        File.WriteAllText(Path.Combine(workspace.DataDir, "feedarr.db"), "live database must not be opened");
        var pending = new PendingRestoreStore(workspace.DataDir, "feedarr.db");
        var service = CreateBackupService(workspace, db, new SettingsRepository(db), new FakeApiKeyProtectionService(), pending);
        CreateTestArchive(workspace);
        using var copying = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        pending.FaultPoint = phase => { if (phase == "copying-posters") { copying.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); } };
        using var request = new CancellationTokenSource();
        var accepted = service.StartRestorePreparation("job.zip", "1.0.0");
        try
        {
            Assert.True(copying.Wait(TimeSpan.FromSeconds(5)));
            request.Cancel(); // Closing the accepted HTTP request no longer owns the job token.
            Assert.Equal("running", service.GetOperationState().Preparation?.State);
            Assert.True(service.GetOperationState().IsBusy);
            Assert.False(service.GetPendingRestore().Pending);
            Assert.Equal(409, Assert.Throws<BackupOperationException>(() => service.StartRestorePreparation("job.zip", "1.0.0")).StatusCode);
            Assert.Equal(409, (await Assert.ThrowsAsync<BackupOperationException>(() => service.CreateBackupAsync("1.0.0"))).StatusCode);
        }
        finally { release.Set(); }
        await WaitForPreparation(service);
        Assert.Equal("completed", service.GetOperationState().Preparation?.State);
        Assert.Equal(accepted.Id, service.GetPendingRestore().Id);
        Assert.Equal(accepted.Id, new PendingRestoreStore(workspace.DataDir, "feedarr.db").GetStatus().Id);
        Assert.Equal("live database must not be opened", File.ReadAllText(Path.Combine(workspace.DataDir, "feedarr.db")));
        await service.StopAsync(default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationFailureOrTimeout_LeavesNoPendingAndReleasesBusyState(bool timeout)
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        var pending = new PendingRestoreStore(workspace.DataDir, "feedarr.db");
        var service = CreateBackupService(workspace, db, new SettingsRepository(db), new FakeApiKeyProtectionService(), pending);
        CreateTestArchive(workspace);
        if (timeout) service.PreparationTimeout = TimeSpan.FromMilliseconds(1);
        pending.FaultPoint = phase =>
        {
            if (phase != "copying-posters") return;
            if (timeout) Thread.Sleep(30);
            else throw new IOException("simulated storage failure");
        };
        service.StartRestorePreparation("job.zip", "1.0.0");
        await WaitForPreparation(service);
        var state = service.GetOperationState();
        Assert.Equal("failed", state.Preparation?.State);
        Assert.NotEmpty(state.Preparation!.Error!);
        Assert.False(state.IsBusy);
        Assert.False(state.RestorePending!.Pending);
        Assert.Empty(Directory.GetDirectories(Path.Combine(workspace.DataDir, "restore-state"), "building-*"));
        await service.StopAsync(default);
    }

    [Theory]
    [InlineData("building")]
    [InlineData("orphan-stage")]
    [InlineData("pending-temp")]
    public void PreparationPreflight_RejectsIncompleteStagingWithoutDeletingIt(string kind)
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        var service = CreateBackupService(workspace, db, new SettingsRepository(db), new FakeApiKeyProtectionService());
        CreateTestArchive(workspace);
        var root = Path.Combine(workspace.DataDir, "restore-state");
        var path = kind switch
        {
            "orphan-stage" => Path.Combine(root, "stages", Guid.NewGuid().ToString("N"), "incoming.db"),
            "pending-temp" => Path.Combine(root, "pending.json.tmp"),
            _ => Path.Combine(root, "building-" + Guid.NewGuid().ToString("N"), "incoming.db")
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "diagnose first");
        Assert.Equal(409, Assert.Throws<BackupOperationException>(() => service.StartRestorePreparation("job.zip", "1.0.0")).StatusCode);
        Assert.Equal("diagnose first", File.ReadAllText(path));
        Assert.Null(service.GetOperationState().Preparation);
    }

    [Fact]
    public async Task FailedBackgroundReplacement_KeepsThePreviouslyPublishedManifest()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        var pending = new PendingRestoreStore(workspace.DataDir, "feedarr.db");
        var service = CreateBackupService(workspace, db, new SettingsRepository(db), new FakeApiKeyProtectionService(), pending);
        CreateTestArchive(workspace);
        await service.RestoreBackupAsync("job.zip", "1.0.0");
        var previous = service.GetPendingRestore();
        Assert.Equal(409, Assert.Throws<BackupOperationException>(() => service.StartRestorePreparation("job.zip", "1.0.0")).StatusCode);
        pending.FaultPoint = phase => { if (phase == "copying-posters") throw new IOException("replacement failed"); };
        service.StartRestorePreparation("job.zip", "1.0.0", previous.Id);
        await WaitForPreparation(service);
        Assert.Equal("failed", service.GetOperationState().Preparation?.State);
        Assert.Equal(previous.Id, service.GetPendingRestore().Id);
        await service.StopAsync(default);
    }

    [Fact]
    public async Task HostShutdown_CancelsPreparationWithoutPublishingOrTouchingActiveDatabase()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        File.WriteAllText(Path.Combine(workspace.DataDir, "feedarr.db"), "live");
        var pending = new PendingRestoreStore(workspace.DataDir, "feedarr.db");
        var service = CreateBackupService(workspace, db, new SettingsRepository(db), new FakeApiKeyProtectionService(), pending);
        CreateTestArchive(workspace);
        using var copying = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        pending.FaultPoint = phase => { if (phase == "copying-posters") { copying.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); } };
        service.StartRestorePreparation("job.zip", "1.0.0");
        Task stopped;
        try
        {
            Assert.True(copying.Wait(TimeSpan.FromSeconds(5)));
            stopped = service.StopAsync(default);
        }
        finally { release.Set(); }
        await stopped;
        Assert.Equal("failed", service.GetOperationState().Preparation?.State);
        Assert.Contains("arrêt du serveur", service.GetOperationState().Preparation?.Error);
        Assert.False(service.GetPendingRestore().Pending);
        Assert.Equal("live", File.ReadAllText(Path.Combine(workspace.DataDir, "feedarr.db")));
    }

    private static void CreateTestArchive(TestWorkspace workspace)
    {
        var incoming = Path.Combine(workspace.RootDir, "job.db");
        CreateIncomingBackupDatabase(incoming);
        Directory.CreateDirectory(workspace.BackupsDir);
        using var zip = ZipFile.Open(Path.Combine(workspace.BackupsDir, "job.zip"), ZipArchiveMode.Create);
        zip.CreateEntryFromFile(incoming, "feedarr.db");
        AddJsonEntry(zip, "info.json", new { version = "1.0.0", backupFormatVersion = 1, dbSha256 = ComputeFileSha256(incoming) });
    }

    private static async Task WaitForPreparation(BackupService service)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (service.GetOperationState().Preparation?.State == "running")
            await Task.Delay(10, deadline.Token);
    }

    private static BackupService CreateBackupService(
        TestWorkspace workspace,
        Db db,
        SettingsRepository settings,
        IApiKeyProtectionService protection, PendingRestoreStore? pending = null)
    {
        return new BackupService(
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
            NullLogger<BackupService>.Instance, pending);
    }

    private static void EnsureSettingsTable(Db db)
    {
        using var conn = db.Open();
        conn.Execute(
            """
            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT PRIMARY KEY,
                value_json TEXT,
                updated_at_ts INTEGER NOT NULL
            );
            """);
    }

    private static void CreateIncomingBackupDatabase(string dbPath)
    {
        if (File.Exists(dbPath))
            File.Delete(dbPath);

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        using var conn = new SqliteConnection(cs);
        conn.Open();

        conn.Execute(
            """
            CREATE TABLE app_settings (
                key TEXT PRIMARY KEY,
                value_json TEXT,
                updated_at_ts INTEGER NOT NULL
            );

            CREATE TABLE sources (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                api_key TEXT NOT NULL
            );

            CREATE TABLE providers (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                api_key TEXT NOT NULL
            );

            CREATE TABLE arr_applications (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                api_key_encrypted TEXT NOT NULL
            );
            """);

        conn.Execute("INSERT INTO sources(api_key) VALUES (@v);", new { v = "plain-source-key" });
        conn.Execute("INSERT INTO providers(api_key) VALUES (@v);", new { v = "ENC:BROKEN" });
        conn.Execute("INSERT INTO arr_applications(api_key_encrypted) VALUES (@v);", new { v = "plain-arr-key" });
    }

    private static void AddJsonEntry(ZipArchive archive, string name, object payload)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(JsonSerializer.Serialize(payload));
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class FakeApiKeyProtectionService : IApiKeyProtectionService
    {
        public string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return plainText;
            if (IsProtected(plainText))
                return plainText;
            return "ENC:PROT:" + plainText;
        }

        public string Unprotect(string protectedText)
        {
            if (TryUnprotect(protectedText, out var plainText))
                return plainText;
            return protectedText;
        }

        public bool TryUnprotect(string protectedText, out string plainText)
        {
            plainText = protectedText;
            if (string.IsNullOrEmpty(protectedText))
                return true;
            if (!IsProtected(protectedText))
                return true;
            if (protectedText.StartsWith("ENC:PROT:", StringComparison.Ordinal))
            {
                plainText = protectedText["ENC:PROT:".Length..];
                return true;
            }

            plainText = protectedText;
            return false;
        }

        public bool IsProtected(string value)
            => !string.IsNullOrEmpty(value) && value.StartsWith("ENC:", StringComparison.Ordinal);
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
            BackupsDir = Path.Combine(DataDir, "backups");
            Directory.CreateDirectory(BackupsDir);
        }

        public string RootDir { get; }
        public string DataDir { get; }
        public string BackupsDir { get; }

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
