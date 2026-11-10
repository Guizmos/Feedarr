using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Titles;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class DuplicatePurgeTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public DuplicatePurgeTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void DistinctEpisodes_WithSameParsedTitle_AreNeverDuplicates()
    {
        using var db = new Fixture();
        var first = db.Add("Audit.Show.S01E01.1080p.WEB.Group");
        var second = db.Add("Audit.Show.S01E02.1080p.WEB.Group");
        using var conn = db.Db.Open();
        Assert.Equal(conn.ExecuteScalar<string>("SELECT title_clean FROM releases WHERE id=@first", new { first }),
            conn.ExecuteScalar<string>("SELECT title_clean FROM releases WHERE id=@second", new { second }));
        Assert.Empty(db.Repo.DetectDuplicates().DuplicateIds);
        Assert.Equal(0, db.Repo.PurgeDuplicates([first, second]));
        Assert.Equal(2, db.Count());
    }

    [Theory]
    [InlineData("Audit.Show.S01E01.720p.WEB.Group")]
    [InlineData("Audit.Show.S01E01.1080p.WEB.OtherGroup")]
    [InlineData("Audit.Show.S02E01.1080p.WEB.Group")]
    [InlineData("Audit.Show.S01E01.1080p.WEB.Group.REPACK")]
    public void LegitimateVariants_ArePreserved_EvenWithConflictingHash(string title)
    {
        using var db = new Fixture();
        db.Add(); db.Add(title);
        Assert.Empty(db.Repo.DetectDuplicates().DuplicateIds);
        Assert.Equal(0, db.Repo.PurgeDuplicates(db.AllIds()));
        Assert.Equal(2, db.Count());
    }

    [Fact]
    public void FilmsWithDifferentYears_ArePreserved()
    {
        using var db = new Fixture();
        db.Add("Audit.Movie.1999.1080p.Group"); db.Add("Audit.Movie.2020.1080p.Group");
        Assert.Empty(db.Repo.DetectDuplicates().DuplicateIds);
        Assert.Equal(0, db.Repo.PurgeDuplicates(db.AllIds()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("0000000000000000000000000000000000000000")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("0123456789abcdef0123456789abcdef012345678")]
    public void MissingOrInvalidIdentity_IsNeverPurged(string? hash)
    {
        using var db = new Fixture();
        db.Add(hash: hash); db.Add(hash: hash);
        Assert.Empty(db.Repo.DetectDuplicates().DuplicateIds);
        Assert.Equal(0, db.Repo.PurgeDuplicates(db.AllIds()));
    }

    [Fact]
    public void DifferentHashesOrSources_AreNotDuplicates_RegardlessOfTitleOrGuid()
    {
        using var db = new Fixture();
        db.Add(); db.Add(hash: "abcdef0123456789abcdef0123456789abcdef01"); db.Add(source: 2);
        using var conn = db.Db.Open();
        conn.Execute("UPDATE releases SET guid='shared-provider-guid' WHERE id IN (1,3)");
        Assert.Empty(db.Repo.DetectDuplicates().DuplicateIds);
        Assert.Equal(0, db.Repo.PurgeDuplicates(db.AllIds()));
        Assert.Equal(3, db.Count());
    }

    [Fact]
    public void ThreeCertainDuplicates_NormalizeHash_KeepOneAndAreIdempotent()
    {
        using var db = new Fixture();
        db.Add(); db.Add(hash: Hash.ToUpperInvariant()); var keeper = db.Add(hash: " " + Hash + " ");
        var found = db.Repo.DetectDuplicates();
        Assert.Equal(1, found.GroupsFound);
        Assert.Equal(2, found.DuplicatesCount);
        Assert.Equal(new long[] { 1, 2 }, found.DuplicateIds);
        Assert.Equal(2, db.Repo.PurgeDuplicates(db.AllIds())); // caller cannot delete the keeper
        Assert.Equal(new[] { keeper }, db.AllIds());
        Assert.Empty(db.Repo.DetectDuplicates().DuplicateIds);
        Assert.Equal(0, db.Repo.PurgeDuplicates(found.DuplicateIds));
    }

    [Fact]
    public void RichestReleaseAndItsReferences_AreKept()
    {
        using var db = new Fixture();
        var rich = db.Add(); var sparse = db.Add();
        using var conn = db.Db.Open();
        conn.Execute("""
            INSERT INTO media_entities(id,unified_category,title_clean,created_at_ts,updated_at_ts)
            VALUES(1,'Serie','Audit Show',1,1);
            UPDATE releases SET entity_id=1,ext_overview='valuable metadata',poster_file='kept.jpg',
                poster_store_dir='tmdb-1',poster_key='tmdb:1' WHERE id=@rich;
            INSERT INTO release_arr_status(release_id,in_sonarr,checked_at_ts) VALUES(@rich,1,1);
            """, new { rich });
        var poster = Path.Combine(Path.GetDirectoryName(db.Db.DbPath)!, "kept.jpg");
        File.WriteAllBytes(poster, new byte[] { 1, 2, 3 });
        Assert.Equal(new[] { sparse }, db.Repo.DetectDuplicates().DuplicateIds);
        Assert.Equal(1, db.Repo.PurgeDuplicates([sparse]));
        Assert.Equal(new[] { rich }, db.AllIds());
        Assert.Equal("valuable metadata", conn.ExecuteScalar<string>("SELECT ext_overview FROM releases WHERE id=@rich", new { rich }));
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM poster_store_refs WHERE release_id=@rich", new { rich }));
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM release_arr_status WHERE release_id=@rich", new { rich }));
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM media_entities"));
        Assert.True(File.Exists(poster));
        Assert.Empty(conn.Query("PRAGMA foreign_key_check"));
    }

    [Theory]
    [InlineData("ext_overview", "conflicting metadata")]
    [InlineData("poster_file", "different.jpg")]
    [InlineData("raw_json", "different provider data")]
    [InlineData("seen", "1")]
    public void UniquePersistedInformation_IsNotDiscarded(string column, string value)
    {
        using var db = new Fixture();
        var first = db.Add(); db.Add();
        using var conn = db.Db.Open();
        conn.Execute($"UPDATE releases SET {column}=@value WHERE id=@first", new { value, first });
        if (column != "seen") conn.Execute($"UPDATE releases SET {column}='other' WHERE id<>@first", new { first });
        Assert.Empty(db.Repo.DetectDuplicates().DuplicateIds);
        Assert.Equal(0, db.Repo.PurgeDuplicates(db.AllIds()));
    }

    [Fact]
    public void ConflictingArrStatus_IsNotDiscarded()
    {
        using var db = new Fixture();
        db.Add(); db.Add();
        using var conn = db.Db.Open();
        conn.Execute("INSERT INTO release_arr_status(release_id,in_sonarr,checked_at_ts) VALUES(1,1,1),(2,0,2)");
        Assert.Empty(db.Repo.DetectDuplicates().DuplicateIds);
        Assert.Equal(0, db.Repo.PurgeDuplicates(db.AllIds()));
    }

    [Fact]
    public void SharedPosters_KeepTheirReference_AfterPurge()
    {
        using var db = new Fixture();
        db.Add(); db.Add();
        using var conn = db.Db.Open();
        conn.Execute("UPDATE releases SET poster_store_dir='shared',poster_key='tmdb:1',poster_file='shared.jpg'");
        Assert.Equal(1, db.Repo.PurgeDuplicates(db.Repo.DetectDuplicates().DuplicateIds));
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM poster_store_refs WHERE store_dir='shared'"));
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM poster_store_refs WHERE release_id NOT IN (SELECT id FROM releases)"));
    }

    [Fact]
    public void FailedDeletion_RollsBackReleaseAndAssociationDeletes()
    {
        using var db = new Fixture();
        db.Add(); db.Add();
        using var conn = db.Db.Open();
        conn.Execute("""
            UPDATE releases SET poster_store_dir='shared';
            INSERT INTO release_arr_status(release_id,in_sonarr,checked_at_ts) VALUES(1,1,1),(2,1,1);
            CREATE TRIGGER fail_purge BEFORE DELETE ON releases BEGIN SELECT RAISE(ABORT,'test failure'); END;
            """);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => db.Repo.PurgeDuplicates([1]));
        Assert.Equal(2, db.Count());
        Assert.Equal(2, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM release_arr_status"));
        Assert.Equal(2, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM poster_store_refs"));
    }

    [Fact]
    public void StaleCandidates_AreRevalidatedAfterAnotherConnectionUpdatesIdentity()
    {
        using var db = new Fixture();
        db.Add(); db.Add();
        var stale = db.Repo.DetectDuplicates();
        using (var writer = db.Db.Open())
            writer.Execute("UPDATE releases SET info_hash='abcdef0123456789abcdef0123456789abcdef01' WHERE id=1");
        Assert.Equal(0, db.Repo.PurgeDuplicates(stale.DuplicateIds));
        Assert.Equal(2, db.Count());
    }

    [Fact]
    public async Task ConcurrentWriter_CommitsChangedIdentityBeforePurgeRevalidation()
    {
        using var db = new Fixture();
        db.Add(); db.Add();
        var candidates = db.Repo.DetectDuplicates();
        using var writer = db.Db.Open();
        using var tx = writer.BeginTransaction(deferred: false);
        writer.Execute("UPDATE releases SET info_hash='abcdef0123456789abcdef0123456789abcdef01' WHERE id=1", transaction: tx);
        using var started = new ManualResetEventSlim();
        var purge = Task.Run(() => { started.Set(); return db.Repo.PurgeDuplicates(candidates.DuplicateIds); });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        tx.Commit();
        Assert.Equal(0, await purge.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, db.Count());
    }

    [Fact]
    public void CatalogQueryPlanAndTiming_AreRecorded_OnTemporarySqlite()
    {
        using var db = new Fixture();
        using var conn = db.Db.Open();
        conn.Execute("""
            WITH RECURSIVE numbers(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM numbers WHERE x<20000)
            INSERT INTO releases(source_id,guid,title,title_clean,info_hash,created_at_ts)
            SELECT 1, 'perf-'||x, 'Catalog item '||x, 'same clean title', printf('%040x',x), 1 FROM numbers;
            """);
        using (var tx = conn.BeginTransaction(deferred: true))
        {
            var sql = ReleaseRepository.BuildDuplicateCandidatesSql(conn, tx);
            foreach (var row in conn.Query("EXPLAIN QUERY PLAN " + sql, transaction: tx))
                _output.WriteLine((string)row.detail);
            tx.Commit();
        }
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Assert.Empty(db.Repo.DetectDuplicates().DuplicateIds);
        timer.Stop();
        _output.WriteLine($"Detection, 20,000 valid-hash distinct releases: {timer.ElapsedMilliseconds} ms");
        var legacy = System.Diagnostics.Stopwatch.StartNew();
        var count = conn.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM (
                SELECT ROW_NUMBER() OVER(PARTITION BY source_id,title_clean ORDER BY created_at_ts DESC,id DESC) AS rn FROM releases
            ) WHERE rn>1;
            """);
        legacy.Stop();
        Assert.Equal(19999, count);
        _output.WriteLine($"Previous title-based window query: {legacy.ElapsedMilliseconds} ms (unsafe 19,999 candidates)");
    }

    [Theory]
    [InlineData("year")]
    [InlineData("season")]
    [InlineData("episode")]
    [InlineData("size_bytes")]
    public void ContradictoryFields_OverrideAnIdenticalHashAndOriginalTitle(string column)
    {
        using var db = new Fixture();
        db.Add(); db.Add();
        using var conn = db.Db.Open();
        conn.Execute($"UPDATE releases SET {column}=id");
        Assert.Empty(db.Repo.DetectDuplicates().DuplicateIds);
        Assert.Equal(0, db.Repo.PurgeDuplicates(db.AllIds()));
    }

    [Theory]
    [InlineData("download_url")]
    [InlineData("link")]
    public void ContradictoryMagnetLinks_AreNotDiscarded(string column)
    {
        using var db = new Fixture();
        db.Add(); db.Add();
        using var conn = db.Db.Open();
        conn.Execute($"UPDATE releases SET {column}='magnet:?xt=urn:btih:'||CASE id WHEN 1 THEN '0123456789abcdef0123456789abcdef01234567' ELSE 'abcdef0123456789abcdef0123456789abcdef01' END");
        Assert.Empty(db.Repo.DetectDuplicates().DuplicateIds);
        Assert.Equal(0, db.Repo.PurgeDuplicates(db.AllIds()));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "feedarr-duplicates", Guid.NewGuid().ToString("N"));
        internal Db Db { get; }
        internal ReleaseRepository Repo { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Db = new Db(OptionsFactory.Create(new AppOptions { DataDir = _root, DbFileName = "test.db" }));
            new MigrationsRunner(Db, NullLogger<MigrationsRunner>.Instance).Run();
            Repo = new ReleaseRepository(Db, new TitleParser(), new UnifiedCategoryResolver());
            using var conn = Db.Open();
            conn.Execute("INSERT INTO sources(id,name,torznab_url,api_key,created_at_ts,updated_at_ts) VALUES(1,'test','https://one.test','',1,1),(2,'test2','https://two.test','',1,1)");
        }
        internal long Add(string title = "Audit.Show.S01E01.1080p.WEB.Group", string? hash = Hash, int source = 1)
        {
            var parsed = new TitleParser().Parse(title, Feedarr.Api.Models.UnifiedCategory.Serie);
            using var conn = Db.Open();
            return conn.ExecuteScalar<long>("""
                INSERT INTO releases(source_id,guid,title,title_clean,info_hash,created_at_ts,season,episode,year,resolution,release_group)
                VALUES(@source,@guid,@title,@clean,@hash,1,@season,@episode,@year,@resolution,@releaseGroup);
                SELECT last_insert_rowid();
                """, new { source, guid = Guid.NewGuid().ToString(), title, clean = parsed.TitleClean, hash,
                    season = parsed.Season, episode = parsed.Episode, year = parsed.Year,
                    resolution = parsed.Resolution, releaseGroup = parsed.ReleaseGroup });
        }
        internal List<long> AllIds() { using var conn = Db.Open(); return conn.Query<long>("SELECT id FROM releases ORDER BY id").AsList(); }
        internal int Count() { using var conn = Db.Open(); return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM releases"); }
        public void Dispose() { Directory.Delete(_root, true); }
    }
}
