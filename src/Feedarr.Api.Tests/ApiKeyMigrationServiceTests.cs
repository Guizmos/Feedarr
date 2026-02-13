using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Security;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class ApiKeyMigrationServiceTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    [Fact]
    public async Task MigrateAsync_EncryptsLegacyKeys_AndRemainsIdempotent()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        EnsureTables(db);

        using (var conn = db.Open())
        {
            conn.Execute("INSERT INTO sources(id, api_key) VALUES (1, 'source-legacy');");
            conn.Execute("INSERT INTO providers(id, api_key) VALUES (1, 'provider-legacy');");
            conn.Execute("INSERT INTO arr_applications(id, api_key_encrypted) VALUES (1, 'arr-legacy');");
            conn.Execute(
                "INSERT INTO app_settings(key, value_json, updated_at_ts) VALUES ('tmdb_api_key', @value, @ts);",
                new { value = JsonSerializer.Serialize("tmdb-legacy", JsonOpts), ts = 0L });
            conn.Execute(
                "INSERT INTO app_settings(key, value_json, updated_at_ts) VALUES ('external', @value, @ts);",
                new
                {
                    value = JsonSerializer.Serialize(new ExternalSettings
                    {
                        FanartApiKey = "fanart-legacy",
                        IgdbClientSecret = "igdb-legacy"
                    }, JsonOpts),
                    ts = 0L
                });
        }

        var service = new ApiKeyMigrationService(
            db,
            new FakeApiKeyProtectionService(),
            NullLogger<ApiKeyMigrationService>.Instance);

        await service.MigrateAsync();

        var firstPass = ReadKeys(db);
        Assert.StartsWith("ENC:PROT:", firstPass.Source);
        Assert.StartsWith("ENC:PROT:", firstPass.Provider);
        Assert.StartsWith("ENC:PROT:", firstPass.ArrApp);
        Assert.StartsWith("ENC:PROT:", firstPass.TmdbSetting);
        Assert.StartsWith("ENC:PROT:", firstPass.LegacyExternalFanart);
        Assert.StartsWith("ENC:PROT:", firstPass.LegacyExternalIgdbSecret);

        await service.MigrateAsync();

        var secondPass = ReadKeys(db);
        Assert.Equal(firstPass.Source, secondPass.Source);
        Assert.Equal(firstPass.Provider, secondPass.Provider);
        Assert.Equal(firstPass.ArrApp, secondPass.ArrApp);
        Assert.Equal(firstPass.TmdbSetting, secondPass.TmdbSetting);
        Assert.Equal(firstPass.LegacyExternalFanart, secondPass.LegacyExternalFanart);
        Assert.Equal(firstPass.LegacyExternalIgdbSecret, secondPass.LegacyExternalIgdbSecret);
    }

    [Fact]
    public async Task MigrateAsync_Throws_WhenProtectionFails()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        EnsureTables(db);

        using (var conn = db.Open())
        {
            conn.Execute("INSERT INTO sources(id, api_key) VALUES (1, 'source-legacy');");
            conn.Execute("INSERT INTO providers(id, api_key) VALUES (1, 'explode');");
        }

        var service = new ApiKeyMigrationService(
            db,
            new FailingProtectionService("explode"),
            NullLogger<ApiKeyMigrationService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.MigrateAsync());
        Assert.Contains("migration failed", ex.Message, StringComparison.OrdinalIgnoreCase);

        using (var conn = db.Open())
        {
            var source = conn.ExecuteScalar<string?>("SELECT api_key FROM sources WHERE id = 1;");
            var provider = conn.ExecuteScalar<string?>("SELECT api_key FROM providers WHERE id = 1;");
            Assert.Equal("source-legacy", source);
            Assert.Equal("explode", provider);
        }

        var backupDir = Path.Combine(workspace.DataDir, "backups");
        Assert.True(Directory.Exists(backupDir));
        Assert.NotEmpty(Directory.GetFiles(backupDir, "feedarr-pre-api-key-migration-*.db"));
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

    private static void EnsureTables(Db db)
    {
        using var conn = db.Open();
        conn.Execute(
            """
            CREATE TABLE IF NOT EXISTS sources (
                id INTEGER PRIMARY KEY,
                api_key TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS providers (
                id INTEGER PRIMARY KEY,
                api_key TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS arr_applications (
                id INTEGER PRIMARY KEY,
                api_key_encrypted TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT PRIMARY KEY,
                value_json TEXT NOT NULL,
                updated_at_ts INTEGER NOT NULL
            );
            """);
    }

    private static (string Source, string Provider, string ArrApp, string TmdbSetting, string LegacyExternalFanart, string LegacyExternalIgdbSecret) ReadKeys(Db db)
    {
        using var conn = db.Open();
        var source = conn.ExecuteScalar<string?>("SELECT api_key FROM sources WHERE id = 1;") ?? "";
        var provider = conn.ExecuteScalar<string?>("SELECT api_key FROM providers WHERE id = 1;") ?? "";
        var arrApp = conn.ExecuteScalar<string?>("SELECT api_key_encrypted FROM arr_applications WHERE id = 1;") ?? "";
        var tmdbRaw = conn.ExecuteScalar<string?>("SELECT value_json FROM app_settings WHERE key = 'tmdb_api_key';") ?? "";
        var tmdbSetting = JsonSerializer.Deserialize<string>(tmdbRaw, JsonOpts) ?? "";

        var legacyRaw = conn.ExecuteScalar<string?>("SELECT value_json FROM app_settings WHERE key = 'external';") ?? "";
        var legacy = JsonSerializer.Deserialize<ExternalSettings>(legacyRaw, JsonOpts) ?? new ExternalSettings();

        return (
            source,
            provider,
            arrApp,
            tmdbSetting,
            legacy.FanartApiKey ?? "",
            legacy.IgdbClientSecret ?? "");
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

            return false;
        }

        public bool IsProtected(string value)
            => !string.IsNullOrEmpty(value) && value.StartsWith("ENC:", StringComparison.Ordinal);
    }

    private sealed class FailingProtectionService : IApiKeyProtectionService
    {
        private readonly string _valueToFail;

        public FailingProtectionService(string valueToFail)
        {
            _valueToFail = valueToFail;
        }

        public string Protect(string plainText)
        {
            if (string.Equals(plainText, _valueToFail, StringComparison.Ordinal))
                throw new InvalidOperationException("protection failure");
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
            return true;
        }

        public bool IsProtected(string value)
            => !string.IsNullOrEmpty(value) && value.StartsWith("ENC:", StringComparison.Ordinal);
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
