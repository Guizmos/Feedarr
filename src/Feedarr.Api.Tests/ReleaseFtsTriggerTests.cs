using System.Text.Json;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Torznab;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SQLitePCL;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class ReleaseFtsTriggerTests
{
    private const string Migration = "0063_fts_updates_only_indexed_changes.sql";

    [Fact]
    public void HundredNonIndexedUpdatesDoNotRewriteFtsDocuments()
    {
        using var f = new Fixture(); f.Insert("Amélie"); f.Observe();
        for (var i = 0; i < 100; i++)
        {
            if (i < 50) f.Connection.Execute("UPDATE releases SET seen=@seen,seeders=@i,grabs=@i WHERE id=1", new { i, seen = i % 2 });
            else f.Connection.Execute("UPDATE releases SET ext_overview=@text,ext_rating=@i,poster_last_attempt_ts=@i WHERE id=1", new { i, text = "fixture-" + i });
        }
        Console.WriteLine($"100 counters/metadata updates: FTS document deletes={f.Deletes}, inserts={f.Inserts}");
        Assert.Equal(0, f.Deletes); Assert.Equal(0, f.Inserts);
        Assert.Equal(1, f.Match("Amelie")); f.CheckIntegrity();
        for (var i = 0; i < 100; i++)
            f.Connection.Execute("UPDATE releases SET title=title,title_clean=title_clean,grabs=@i WHERE id=1", new { i });
        Assert.Equal(0, f.Deletes); Assert.Equal(0, f.Inserts); f.CheckIntegrity();
    }

    [Fact]
    public void IndexedChangesInsertAndDeleteKeepUnicodeSearchCoherent()
    {
        using var f = new Fixture(); f.Observe(); f.Insert("Amélie");
        Assert.Equal(1, f.Inserts); Assert.Equal(0, f.Deletes);
        Assert.Equal(1, f.Match("Amelie")); Assert.Equal(1, f.Match("Amélie"));
        f.ResetCounts(); f.Connection.Execute("UPDATE releases SET title='Pokémon' WHERE id=1");
        Assert.Equal(1, f.Deletes); Assert.Equal(1, f.Inserts);
        Assert.Equal(0, f.Match("Amelie")); Assert.Equal(1, f.Match("Pokemon")); Assert.Equal(1, f.Match("Pokémon"));
        f.ResetCounts(); f.Connection.Execute("UPDATE releases SET title_clean='Salamèche' WHERE id=1");
        Assert.Equal(1, f.Deletes); Assert.Equal(1, f.Inserts);
        Assert.Equal(1, f.Match("Salameche")); f.CheckIntegrity();
        f.Connection.Execute("UPDATE releases SET title_clean=NULL WHERE id=1");
        Assert.Equal(0, f.Match("Salameche")); f.CheckIntegrity();
        f.ResetCounts(); f.Connection.Execute("UPDATE releases SET title_clean='' WHERE id=1");
        Assert.Equal(0, f.Deletes); Assert.Equal(0, f.Inserts); // Same COALESCE value indexed.
        // The content rowid is part of index identity even though it is not tokenized.
        f.Connection.Execute("UPDATE releases SET id=5 WHERE id=1");
        Assert.Equal(1, f.Deletes); Assert.Equal(1, f.Inserts);
        Assert.Equal(5, f.Connection.ExecuteScalar<long>("SELECT rowid FROM releases_fts WHERE releases_fts MATCH 'pokemon'"));
        f.CheckIntegrity(); f.ResetCounts();
        f.Connection.Execute("DELETE FROM releases WHERE id=5");
        Assert.Equal(1, f.Deletes); Assert.Equal(0, f.Inserts);
        Assert.Equal(0, f.Match("Pokemon")); Assert.Equal(0, f.Match("Salameche")); f.CheckIntegrity();
    }

    [Fact]
    public void RepeatedRealRssUpsertsDoNotRewriteUnchangedTitles()
    {
        using var f = new Fixture();
        var repository = new ReleaseRepository(f.Db, new TitleParser(), new UnifiedCategoryResolver());
        void Sync(string title) => repository.UpsertMany(1, "fixture", [new TorznabItem { Guid = "rss", Title = title, CategoryId = 2000 }],
            categoryMap: new() { [2000] = ("films", "Films") });
        Sync("Amélie (2001) 1080p BluRay x264-GRP");
        var before = f.IndexSnapshot();
        for (var i = 0; i < 100; i++) Sync("Amélie (2001) 1080p BluRay x264-GRP");
        Assert.Equal(before, f.IndexSnapshot());
        // Upserts use their own connection; also check the full external-content index.
        Assert.Equal(1, f.Match("Amelie")); f.CheckIntegrity();
        Sync("Pokémon (2019) 1080p BluRay x264-GRP");
        Assert.Equal(0, f.Match("Amelie")); Assert.Equal(1, f.Match("Pokemon")); f.CheckIntegrity();
        for (var i = 0; i < 10; i++) Sync("Pokémon (2019) 1080p BluRay x264-GRP");
        Assert.Equal(1, f.Match("Pokemon")); f.CheckIntegrity();
    }

    [Fact]
    public void ExistingDatabaseMigrationPreservesIndexAndDoesNotRebuildOnRestart()
    {
        using var f = new Fixture();
        // Recreate the exact pre-0063 trigger and migration ledger, then populate it.
        var legacy = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "Migrations", "0027_releases_search_fts.sql"));
        f.Connection.Execute("DROP TRIGGER releases_au");
        f.Connection.Execute(legacy[legacy.IndexOf("CREATE TRIGGER IF NOT EXISTS releases_au", StringComparison.Ordinal)..]);
        f.Connection.Execute("DELETE FROM schema_migrations WHERE id=@id", new { id = Migration });
        f.Insert("Amélie"); f.Observe();
        for (var i = 1; i <= 100; i++)
            f.Connection.Execute("UPDATE releases SET seeders=@i WHERE id=1", new { i });
        Console.WriteLine($"Before migration, 100 updates: FTS deletes={f.Deletes}, inserts={f.Inserts}");
        Assert.Equal(100, f.Deletes); Assert.Equal(100, f.Inserts);
        var before = f.IndexSnapshot();
        f.Migrate();
        Assert.Equal(before, f.IndexSnapshot());
        Assert.Equal(1, f.Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM schema_migrations WHERE id=@id", new { id = Migration }));
        f.ResetCounts();
        for (var i = 101; i <= 200; i++)
            f.Connection.Execute("UPDATE releases SET seeders=@i WHERE id=1", new { i });
        Console.WriteLine($"After migration, 100 updates: FTS deletes={f.Deletes}, inserts={f.Inserts}");
        Assert.Equal(0, f.Deletes); Assert.Equal(0, f.Inserts);
        Assert.Equal(1, f.Match("Amelie")); f.CheckIntegrity();
        before = f.IndexSnapshot(); f.Migrate();
        Assert.Equal(before, f.IndexSnapshot());
        f.Connection.Execute("UPDATE releases SET title='Pokémon' WHERE id=1");
        Assert.Equal(0, f.Match("Amelie")); Assert.Equal(1, f.Match("Pokemon")); f.CheckIntegrity();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "feedarr-fts-" + Guid.NewGuid().ToString("N"));
        public readonly Db Db;
        public readonly SqliteConnection Connection;
        public int Deletes, Inserts;
        public Fixture()
        {
            Db = new Db(OptionsFactory.Create(new AppOptions { DataDir = _dir, DbFileName = "fixture.db" }));
            Migrate(); Connection = Db.Open();
            Connection.Execute("INSERT INTO sources(id,name,enabled,torznab_url,api_key,auth_mode,created_at_ts,updated_at_ts) VALUES(1,'fixture',1,'http://fixture.test','fixture','query',1,1)");
        }
        public void Migrate() => new MigrationsRunner(Db, NullLogger<MigrationsRunner>.Instance).Run();
        public void Insert(string title) => Connection.Execute("INSERT INTO releases(source_id,guid,title,created_at_ts) VALUES(1,'fixture',@title,1)", new { title });
        public long Match(string text) => Connection.ExecuteScalar<long>("SELECT COUNT(*) FROM releases_fts WHERE releases_fts MATCH @q", new { q = "\"" + text + "\"*" });
        public void CheckIntegrity() => Connection.Execute("INSERT INTO releases_fts(releases_fts,rank) VALUES('integrity-check',1)");
        public void ResetCounts() { Deletes = 0; Inserts = 0; }
        public void Observe()
        {
            // Native hook, no instrumentation writes: FTS5 stores one docsize row
            // per indexed document. Observe actual document removal/insertion.
            raw.sqlite3_update_hook(Connection.Handle!, (_, operation, _, table, _) =>
            {
                if (table.utf8_to_string() != "releases_fts_docsize") return;
                if (operation == raw.SQLITE_DELETE) Deletes++;
                if (operation == raw.SQLITE_INSERT) Inserts++;
            }, null);
        }
        public string IndexSnapshot() => JsonSerializer.Serialize(Connection.Query("SELECT id,hex(block) block FROM releases_fts_data ORDER BY id"));
        public void Dispose()
        {
            raw.sqlite3_update_hook(Connection.Handle!, (delegate_update)null!, null); Connection.Dispose();
            SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true);
        }
    }
}
