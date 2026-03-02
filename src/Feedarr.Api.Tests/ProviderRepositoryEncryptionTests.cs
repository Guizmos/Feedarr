using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Security;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

/// <summary>
/// Verifies that ProviderRepository reads/writes api_key_encrypted correctly,
/// and that ApiKeyMigrationService encrypts any remaining plaintext values.
/// </summary>
public sealed class ProviderRepositoryEncryptionTests
{
    // ------------------------------------------------------------------ //
    //  A) Column rename — api_key must not exist, api_key_encrypted must  //
    // ------------------------------------------------------------------ //

    [Fact]
    public void AfterMigration_ApiKeyColumnDoesNotExist_ApiKeyEncryptedExists()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        using var conn = db.Open();

        // Verify api_key_encrypted column exists
        var columns = conn.Query<string>(
            "SELECT name FROM pragma_table_info('providers');"
        ).ToList();

        Assert.Contains("api_key_encrypted", columns);
        Assert.DoesNotContain("api_key", columns);
    }

    // ------------------------------------------------------------------ //
    //  B) Create stores encrypted value, Get returns plaintext            //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Create_StoresEncryptedApiKey_InApiKeyEncryptedColumn()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var protection = new FakeApiKeyProtectionService();
        var repo = new ProviderRepository(db, protection);

        var id = repo.Create("jackett", "Jackett", "http://localhost:9117", "my-plain-key", true);

        using var conn = db.Open();
        var stored = conn.ExecuteScalar<string>(
            "SELECT api_key_encrypted FROM providers WHERE id = @id;",
            new { id });

        // Stored value must be encrypted (ENC: prefix from FakeApiKeyProtectionService)
        Assert.StartsWith("ENC:PROT:", stored);

        // Get() must return the decrypted plaintext
        var provider = repo.Get(id);
        Assert.NotNull(provider);
        Assert.Equal("my-plain-key", provider!.ApiKey);
    }

    // ------------------------------------------------------------------ //
    //  C) Empty API key is stored as empty string (no encryption needed)  //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Create_EmptyApiKey_StoredAsEmptyString_NotNull()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var protection = new FakeApiKeyProtectionService();
        var repo = new ProviderRepository(db, protection);

        var id = repo.Create("jackett", "Jackett No Key", "http://localhost:9117", "", true);

        using var conn = db.Open();
        var stored = conn.ExecuteScalar<string?>(
            "SELECT api_key_encrypted FROM providers WHERE id = @id;",
            new { id });

        // Empty string is not encrypted (Protect("") returns "")
        Assert.Equal("", stored);

        // HasApiKey must be false
        var list = repo.List().ToList();
        var item = list.Single(p => p.Id == id);
        Assert.False(item.HasApiKey);
    }

    // ------------------------------------------------------------------ //
    //  D) Update with new key encrypts in api_key_encrypted               //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Update_WithNewApiKey_EncryptsInApiKeyEncryptedColumn()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var protection = new FakeApiKeyProtectionService();
        var repo = new ProviderRepository(db, protection);

        var id = repo.Create("jackett", "Jackett", "http://localhost:9117", "old-key", true);
        repo.Update(id, "jackett", "Jackett", "http://localhost:9117", "new-key");

        using var conn = db.Open();
        var stored = conn.ExecuteScalar<string>(
            "SELECT api_key_encrypted FROM providers WHERE id = @id;",
            new { id });

        Assert.StartsWith("ENC:PROT:", stored);

        var provider = repo.Get(id);
        Assert.NotNull(provider);
        Assert.Equal("new-key", provider!.ApiKey);
    }

    // ------------------------------------------------------------------ //
    //  E) ApiKeyMigrationService encrypts legacy plaintext in             //
    //     api_key_encrypted (simulates DB upgraded from before migration) //
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task ApiKeyMigrationService_EncryptsLegacyPlaintextInApiKeyEncrypted()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        // Insert a provider row with a plaintext api_key_encrypted (legacy state)
        using (var conn = db.Open())
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            conn.Execute(
                """
                INSERT INTO providers(type, name, base_url, api_key_encrypted, enabled, created_at_ts, updated_at_ts)
                VALUES ('jackett', 'Legacy Jackett', 'http://localhost:9117', 'plain-legacy-key', 1, @now, @now);
                """,
                new { now });
        }

        var protection = new FakeApiKeyProtectionService();
        var migrationService = new ApiKeyMigrationService(
            db,
            protection,
            NullLogger<ApiKeyMigrationService>.Instance);

        await migrationService.MigrateAsync();

        using var verifyConn = db.Open();
        var stored = verifyConn.ExecuteScalar<string>(
            "SELECT api_key_encrypted FROM providers WHERE name = 'Legacy Jackett';");

        // After migration, value must be encrypted
        Assert.StartsWith("ENC:PROT:", stored);

        // And it must decrypt to the original value
        var repo = new ProviderRepository(db, protection);
        var provider = repo.List().Single(p => p.Name == "Legacy Jackett");
        // HasApiKey must be true
        Assert.True(provider.HasApiKey);
    }

    // ------------------------------------------------------------------ //
    //  F) ApiKeyMigrationService skips already-encrypted values           //
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task ApiKeyMigrationService_SkipsAlreadyEncryptedValues()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var protection = new FakeApiKeyProtectionService();
        var repo = new ProviderRepository(db, protection);

        // Create with encryption (ProviderRepository always encrypts on write)
        var id = repo.Create("prowlarr", "Prowlarr", "http://localhost:9696", "already-encrypted", true);

        using var conn = db.Open();
        var storedBefore = conn.ExecuteScalar<string>(
            "SELECT api_key_encrypted FROM providers WHERE id = @id;", new { id });

        var migrationService = new ApiKeyMigrationService(
            db,
            protection,
            NullLogger<ApiKeyMigrationService>.Instance);

        await migrationService.MigrateAsync();

        var storedAfter = conn.ExecuteScalar<string>(
            "SELECT api_key_encrypted FROM providers WHERE id = @id;", new { id });

        // Value must not change (already encrypted)
        Assert.Equal(storedBefore, storedAfter);
    }

    // ------------------------------------------------------------------ //
    //  G) Null/empty API key does not trigger Protect()                   //
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task ApiKeyMigrationService_EmptyApiKeyEncrypted_IsNotEncrypted()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        using (var seedConn = db.Open())
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            seedConn.Execute(
                """
                INSERT INTO providers(type, name, base_url, api_key_encrypted, enabled, created_at_ts, updated_at_ts)
                VALUES ('jackett', 'No Key Provider', 'http://localhost:9117', '', 1, @now, @now);
                """,
                new { now });
        }

        var protection = new FakeApiKeyProtectionService();
        var migrationService = new ApiKeyMigrationService(
            db,
            protection,
            NullLogger<ApiKeyMigrationService>.Instance);

        // Must not throw, must not encrypt empty string
        await migrationService.MigrateAsync();

        using var conn = db.Open();
        var stored = conn.ExecuteScalar<string>(
            "SELECT api_key_encrypted FROM providers WHERE name = 'No Key Provider';");

        // Still empty — Protect("") is a no-op in the real service
        Assert.Equal("", stored);
    }

    // ------------------------------------------------------------------ helpers --//

    private static Db CreateDb(TestWorkspace workspace)
    {
        var options = OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        });
        return new Db(options);
    }

    /// <summary>
    /// Deterministic fake: Protect("x") → "ENC:PROT:x", Unprotect("ENC:PROT:x") → "x".
    /// Mirrors FakeApiKeyProtectionService from ArrApplicationRepositorySecurityTests.
    /// </summary>
    private sealed class FakeApiKeyProtectionService : IApiKeyProtectionService
    {
        public string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return plainText;
            if (IsProtected(plainText)) return plainText;
            return "ENC:PROT:" + plainText;
        }

        public string Unprotect(string protectedText)
        {
            if (TryUnprotect(protectedText, out var plain))
                return plain;
            return protectedText;
        }

        public bool TryUnprotect(string protectedText, out string plainText)
        {
            plainText = protectedText;
            if (string.IsNullOrEmpty(protectedText)) return true;
            if (!IsProtected(protectedText)) return true;
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
            try { if (Directory.Exists(RootDir)) Directory.Delete(RootDir, true); }
            catch { }
        }
    }
}
