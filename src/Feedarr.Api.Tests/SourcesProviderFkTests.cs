using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Options;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

/// <summary>
/// Verifies that migration 0048 adds a proper FK constraint on sources.provider_id
/// with ON DELETE SET NULL semantics: deleting a provider nullifies the reference
/// on linked sources without cascade-deleting them.
/// </summary>
public sealed class SourcesProviderFkTests
{
    // ------------------------------------------------------------------ //
    //  A) sources.provider_id FK exists after all migrations              //
    // ------------------------------------------------------------------ //

    [Fact]
    public void AfterMigrations_Sources_HasForeignKeyOnProviderId()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        using var conn = db.Open();

        // SQLite stores FK info in PRAGMA foreign_key_list
        var fkList = conn.Query<FkRow>("PRAGMA foreign_key_list('sources');").ToList();

        var providerFk = fkList.FirstOrDefault(r =>
            string.Equals(r.Table, "providers", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.From, "provider_id", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(providerFk);
        Assert.Equal("SET NULL", providerFk!.On_Delete, StringComparer.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ //
    //  B) Deleting a provider NULLifies sources.provider_id (not CASCADE) //
    // ------------------------------------------------------------------ //

    [Fact]
    public void DeleteProvider_NullifiesSourceProviderIdButPreservesSource()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        using var conn = db.Open();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Insert a provider
        var providerId = conn.ExecuteScalar<long>(
            """
            INSERT INTO providers(type, name, base_url, api_key_encrypted, enabled, created_at_ts, updated_at_ts)
            VALUES ('jackett', 'Test Provider', 'http://localhost:9117', '', 1, @now, @now);
            SELECT last_insert_rowid();
            """,
            new { now });

        // Insert a source linked to that provider
        var sourceId = conn.ExecuteScalar<long>(
            """
            INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts, provider_id)
            VALUES ('Test Source', 1, 'http://localhost:9117/api', 'key', 'query', @now, @now, @providerId);
            SELECT last_insert_rowid();
            """,
            new { now, providerId });

        // Confirm the FK is set
        var storedProviderId = conn.ExecuteScalar<long?>(
            "SELECT provider_id FROM sources WHERE id = @id;",
            new { id = sourceId });
        Assert.Equal(providerId, storedProviderId);

        // Delete the provider — FK ON DELETE SET NULL should NULL-ify source.provider_id
        conn.Execute("DELETE FROM providers WHERE id = @id;", new { id = providerId });

        // Source must still exist
        var sourceCount = conn.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM sources WHERE id = @id;",
            new { id = sourceId });
        Assert.Equal(1, sourceCount);

        // Source.provider_id must be NULL (not deleted, not still referencing the provider)
        var newProviderId = conn.ExecuteScalar<long?>(
            "SELECT provider_id FROM sources WHERE id = @id;",
            new { id = sourceId });
        Assert.Null(newProviderId);
    }

    // ------------------------------------------------------------------ //
    //  C) releases are NOT cascade-deleted when sources is rebuilt        //
    //     (the migration itself must not wipe releases data)              //
    // ------------------------------------------------------------------ //

    [Fact]
    public void AfterMigration0048_ExistingReleasesArePreserved()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        using var conn = db.Open();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var sourceId = conn.ExecuteScalar<long>(
            """
            INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
            VALUES ('Preserved Source', 1, 'http://localhost:9117/api', 'k', 'query', @now, @now);
            SELECT last_insert_rowid();
            """,
            new { now });

        conn.Execute(
            """
            INSERT INTO releases(source_id, guid, title, created_at_ts, seen)
            VALUES (@sourceId, 'preserved-guid-1', 'Test Release', @now, 0);
            """,
            new { sourceId, now });

        var count = conn.ExecuteScalar<long>("SELECT COUNT(1) FROM releases;");
        Assert.Equal(1, count);
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

    private sealed class FkRow
    {
        public int Id { get; init; }
        public int Seq { get; init; }
        public string Table { get; init; } = string.Empty;
        public string From { get; init; } = string.Empty;
        public string To { get; init; } = string.Empty;
        public string On_Update { get; init; } = string.Empty;
        public string On_Delete { get; init; } = string.Empty;
        public string Match { get; init; } = string.Empty;
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
