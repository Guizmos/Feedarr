using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Security;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class SourceCategoryMappingsBackfillMigrationTests
{
    [Fact]
    public void Migration_BackfillsSelectedCategories_FromMappedLegacyRows()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        long sourceId;
        using (var conn = db.Open())
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            sourceId = conn.ExecuteScalar<long>(
                """
                INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
                VALUES ('legacy-source', 1, 'http://localhost/legacy', 'key', 'query', @now, @now);
                SELECT last_insert_rowid();
                """,
                new { now });

            conn.Execute(
                """
                INSERT INTO source_categories(source_id, cat_id, name, parent_cat_id, is_sub, last_seen_at_ts, unified_key, unified_label)
                VALUES
                    (@sid, 5000, 'TV', NULL, 0, @now, 'shows', 'Emissions'),
                    (@sid, 7000, 'Books', NULL, 0, @now, NULL, NULL);
                """,
                new { sid = sourceId, now });

            conn.Execute(
                "DELETE FROM source_category_mappings WHERE source_id = @sid;",
                new { sid = sourceId });

            var before = conn.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM source_category_mappings WHERE source_id = @sid;",
                new { sid = sourceId });
            Assert.Equal(0, before);

            var migrationPath = Path.Combine(
                AppContext.BaseDirectory,
                "Data",
                "Migrations",
                "0040_backfill_source_category_mappings_from_legacy.sql");
            var sql = File.ReadAllText(migrationPath);
            var selectedMigrationPath = Path.Combine(
                AppContext.BaseDirectory,
                "Data",
                "Migrations",
                "0041_source_selected_categories.sql");
            var selectedSql = File.ReadAllText(selectedMigrationPath);

            conn.Execute(sql);
            conn.Execute(sql); // idempotence guard
            conn.Execute(selectedSql);
        }

        var repository = new SourceRepository(db, new PassthroughProtectionService());
        var selectedIds = repository.GetSelectedCategoryIds(sourceId).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { 5000 }, selectedIds);

        using (var conn = db.Open())
        {
            var mappings = conn.Query<MappingRow>(
                """
                SELECT
                  cat_id as CatId,
                  group_key as GroupKey,
                  group_label as GroupLabel
                FROM source_category_mappings
                WHERE source_id = @sid
                ORDER BY cat_id;
                """,
                new { sid = sourceId }).ToList();

            Assert.Equal(2, mappings.Count);
            Assert.Equal("emissions", mappings.Single(m => m.CatId == 5000).GroupKey);
            Assert.Equal("Emissions", mappings.Single(m => m.CatId == 5000).GroupLabel);
            Assert.Null(mappings.Single(m => m.CatId == 7000).GroupKey);
        }
    }

    [Fact]
    public void Migration_DoesNotOverrideExistingExplicitSelection()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        long sourceId;
        using (var conn = db.Open())
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            sourceId = conn.ExecuteScalar<long>(
                """
                INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
                VALUES ('legacy-source-2', 1, 'http://localhost/legacy-2', 'key', 'query', @now, @now);
                SELECT last_insert_rowid();
                """,
                new { now });

            conn.Execute(
                """
                INSERT INTO source_category_mappings(source_id, cat_id, group_key, group_label, created_at_ts, updated_at_ts)
                VALUES
                    (@sid, 2000, 'films', 'Films', @now, @now),
                    (@sid, 5000, 'series', 'Série TV', @now, @now);
                """,
                new { sid = sourceId, now });

            conn.Execute(
                """
                INSERT INTO source_selected_categories(source_id, cat_id)
                VALUES (@sid, 7000);
                """,
                new { sid = sourceId });

            var migrationPath = Path.Combine(
                AppContext.BaseDirectory,
                "Data",
                "Migrations",
                "0041_source_selected_categories.sql");
            var sql = File.ReadAllText(migrationPath);

            conn.Execute(sql);
        }

        var repository = new SourceRepository(db, new PassthroughProtectionService());
        var selectedIds = repository.GetSelectedCategoryIds(sourceId).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { 7000 }, selectedIds);
    }

    private sealed class MappingRow
    {
        public int CatId { get; set; }
        public string? GroupKey { get; set; }
        public string? GroupLabel { get; set; }
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

        public bool TryUnprotect(string protectedText, out string? plainText)
        {
            plainText = protectedText;
            return true;
        }

        public bool IsProtected(string value) => false;
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
