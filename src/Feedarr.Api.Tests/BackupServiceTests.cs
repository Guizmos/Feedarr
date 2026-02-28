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

    private static BackupService CreateBackupService(
        TestWorkspace workspace,
        Db db,
        SettingsRepository settings,
        IApiKeyProtectionService protection)
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
            NullLogger<BackupService>.Instance);
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
