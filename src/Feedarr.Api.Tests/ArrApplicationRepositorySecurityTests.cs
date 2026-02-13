using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Security;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class ArrApplicationRepositorySecurityTests
{
    [Fact]
    public void Create_StoresEncryptedApiKey_AndGetReturnsPlaintext()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        EnsureArrApplicationsTable(db);

        var repo = new ArrApplicationRepository(db, new FakeApiKeyProtectionService());
        var id = repo.Create(
            "sonarr",
            "Sonarr Main",
            "http://localhost:8989",
            "plain-arr-key",
            null,
            null,
            "[]",
            "standard",
            true,
            "all",
            true,
            false,
            null,
            true);

        using var conn = db.Open();
        var stored = conn.ExecuteScalar<string>(
            "SELECT api_key_encrypted FROM arr_applications WHERE id = @id;",
            new { id });

        Assert.StartsWith("ENC:PROT:", stored);

        var app = repo.Get(id);
        Assert.NotNull(app);
        Assert.Equal("plain-arr-key", app!.ApiKeyEncrypted);
    }

    [Fact]
    public void Update_ReencryptsLegacyPlaintextWhenApiKeyNotChanged()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        EnsureArrApplicationsTable(db);

        using (var conn = db.Open())
        {
            conn.Execute(
                """
                INSERT INTO arr_applications(
                    type, name, base_url, api_key_encrypted, is_enabled, is_default,
                    root_folder_path, quality_profile_id, tags,
                    series_type, season_folder, monitor_mode, search_missing, search_cutoff,
                    minimum_availability, search_for_movie,
                    created_at_ts, updated_at_ts
                )
                VALUES (
                    'radarr', 'Radarr Main', 'http://localhost:7878', 'legacy-plain-key', 1, 0,
                    NULL, NULL, '[]',
                    NULL, 1, NULL, 1, 0,
                    'released', 1,
                    @now, @now
                );
                """,
                new { now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        }

        var repo = new ArrApplicationRepository(db, new FakeApiKeyProtectionService());
        var existing = repo.List().Single();

        var updated = repo.Update(
            existing.Id,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        Assert.True(updated);

        using var verifyConn = db.Open();
        var stored = verifyConn.ExecuteScalar<string>(
            "SELECT api_key_encrypted FROM arr_applications WHERE id = @id;",
            new { id = existing.Id });

        Assert.StartsWith("ENC:PROT:", stored);
        var app = repo.Get(existing.Id);
        Assert.NotNull(app);
        Assert.Equal("legacy-plain-key", app!.ApiKeyEncrypted);
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

    private static void EnsureArrApplicationsTable(Db db)
    {
        using var conn = db.Open();
        conn.Execute(
            """
            CREATE TABLE IF NOT EXISTS arr_applications (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                type TEXT NOT NULL,
                name TEXT NULL,
                base_url TEXT NOT NULL,
                api_key_encrypted TEXT NOT NULL,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                is_default INTEGER NOT NULL DEFAULT 0,
                root_folder_path TEXT NULL,
                quality_profile_id INTEGER NULL,
                tags TEXT NULL,
                series_type TEXT NULL,
                season_folder INTEGER NOT NULL DEFAULT 1,
                monitor_mode TEXT NULL,
                search_missing INTEGER NOT NULL DEFAULT 1,
                search_cutoff INTEGER NOT NULL DEFAULT 0,
                minimum_availability TEXT NULL,
                search_for_movie INTEGER NOT NULL DEFAULT 1,
                created_at_ts INTEGER NOT NULL,
                updated_at_ts INTEGER NOT NULL
            );
            """);
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
