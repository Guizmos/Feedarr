using System.Text.Json;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Torznab;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class ReleaseRssIdentityTests
{
    [Fact]
    public void ChangedWorkRebindsOnlyTheReleaseAndIsIdempotent()
    {
        using var f = new Fixture();
        f.Sync("a", "Arrival (2016) 1080p BluRay x264-GRP"); f.Sync("sibling", "Arrival (2016) 1080p BluRay x264-GRP");
        f.Sync("target", "Dune (2021) 1080p BluRay x264-GRP");
        f.Stamp("a", 329865); f.Stamp("sibling", 329865); f.Stamp("target", 438631);
        var before = f.Read("a"); var sibling = JsonSerializer.Serialize(f.Read("sibling"));
        var target = JsonSerializer.Serialize(f.Read("target"));
        var stale = f.Repository.GetForPoster(before.Id)!;
        Assert.Equal(0, f.Sync("a", "Dune (2021) 1080p BluRay x264-GRP"));
        var after = f.Read("a");
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(f.Read("target").EntityId, after.EntityId);
        Assert.NotEqual(before.EntityId, after.EntityId);
        Assert.Equal(438631, after.EffectiveTmdbId);
        Assert.Equal("details-438631", after.Overview);
        Assert.Equal("poster-438631.jpg", after.EffectivePoster);
        Assert.Null(after.OwnPoster); Assert.Null(after.OwnTmdbId);
        Assert.Equal(0, f.Scalar("SELECT COUNT(*) FROM release_arr_status WHERE release_id=" + before.Id));
        Assert.Equal(0, f.Scalar("SELECT COUNT(*) FROM poster_store_refs WHERE release_id=" + before.Id));
        using (f.Repository.BeginMatching(stale))
            Assert.Throws<ReleaseRepository.StaleMatchException>(() => f.Repository.ValidateMatching(before.Id));
        Assert.Equal(sibling, JsonSerializer.Serialize(f.Read("sibling")));
        Assert.Equal(target, JsonSerializer.Serialize(f.Read("target")));
        var stable = JsonSerializer.Serialize(after);
        f.Sync("a", "Dune (2021) 1080p BluRay x264-GRP");
        Assert.Equal(stable, JsonSerializer.Serialize(f.Read("a")));
    }

    [Theory]
    [InlineData("Amélie (2001) 1080p BluRay x264-GRP")]
    [InlineData("AMELIE (2001) 2160p BluRay x264-GRP")]
    public void UnchangedOrMinorCorrectionKeepsAssociationAndMetadata(string title)
    {
        using var f = new Fixture(); f.Sync("a", "Amélie (2001) 1080p BluRay x264-GRP"); f.Stamp("a", 194);
        var before = f.Read("a"); f.Sync("a", title);
        var after = f.Read("a");
        Assert.Equal(before.EntityId, after.EntityId);
        Assert.Equal(before.EffectivePoster, after.EffectivePoster);
        Assert.Equal(before.Overview, after.Overview);
        Assert.Equal(before.EffectiveTmdbId, after.EffectiveTmdbId);
        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM release_arr_status"));
        Assert.Empty(f.Log.Warnings);
    }

    [Fact]
    public void ManualAssociationIsPreservedAndConflictIsLogged()
    {
        using var f = new Fixture(); f.Sync("a", "Arrival (2016) 1080p BluRay x264-GRP");
        f.Repository.SetManualMatch(f.Read("a").Id, "tmdb", "movie", "329865"); f.Stamp("a", 329865);
        var before = JsonSerializer.Serialize(f.Read("a"));
        f.Sync("a", "Dune (2021) 1080p BluRay x264-GRP");
        Assert.Equal(before, JsonSerializer.Serialize(f.Read("a")));
        Assert.Contains(f.Log.Warnings, text => text.Contains("rss_identity_conflict"));
        f.Sync("a", "Dune (2021) 1080p BluRay x264-GRP");
        Assert.Equal(before, JsonSerializer.Serialize(f.Read("a")));
    }

    [Fact]
    public void UnchangedRssDoesNotUndoManualMediaTypeOrReportFalseConflict()
    {
        using var f = new Fixture(); f.Sync("a", "Arrival (2016) 1080p BluRay x264-GRP");
        f.Repository.SetManualMatch(f.Read("a").Id, "tmdb", "tv", "123");
        f.Sync("a", "Arrival (2016) 1080p BluRay x264-GRP");
        using var c = f.Db.Open();
        Assert.Equal("series", c.ExecuteScalar<string>("SELECT media_type FROM releases"));
        Assert.Empty(f.Log.Warnings);
    }

    [Fact]
    public void ManualRenameKeepsItsNormalizationAndDedupeIdentity()
    {
        using var f = new Fixture(); f.Sync("a", "Arrival (2016) 1080p BluRay x264-GRP");
        f.Repository.RenameAndRebindEntity(f.Read("a").Id, "Premier Contact (2016)");
        // Capture the manual identity as stored; RSS must not replace its keys.
        var before = f.Read("a");
        Assert.Equal("premier contact", before.Normalized);
        Assert.Contains("premier contact", before.Dedupe);
        f.Sync("a", "Dune (2021) 1080p BluRay x264-GRP");
        var after = f.Read("a");
        Assert.Equal(before.Title, after.Title); Assert.Equal(before.EntityId, after.EntityId);
        Assert.Equal(before.Normalized, after.Normalized); Assert.Equal(before.Dedupe, after.Dedupe);
    }

    [Fact]
    public void NewWorkWithoutYearDoesNotInheritOldYearOrMetadata()
    {
        using var f = new Fixture(); f.Sync("a", "Arrival (2016) 1080p BluRay x264-GRP"); f.Stamp("a", 329865);
        var old = f.Read("a"); Assert.Equal(2016, old.Year); f.Sync("a", "Interstellar 1080p BluRay x264-GRP");
        var row = f.Read("a");
        Assert.NotEqual(old.EntityId, row.EntityId); Assert.Null(row.Year);
        Assert.Null(row.EffectiveTmdbId); Assert.Null(row.EffectivePoster); Assert.Null(row.Overview);
        Assert.Contains(row.Id, f.Repository.GetIdsMissingPoster(10));
        var stable = JsonSerializer.Serialize(row); f.Sync("a", "Interstellar 1080p BluRay x264-GRP");
        Assert.Equal(stable, JsonSerializer.Serialize(f.Read("a")));
    }

    [Fact]
    public void FailedResyncRollsBackAssociationMetadataAndRefs()
    {
        using var f = new Fixture(); f.Sync("a", "Arrival (2016) 1080p BluRay x264-GRP"); f.Stamp("a", 329865);
        var before = JsonSerializer.Serialize(f.Read("a"));
        using (var c = f.Db.Open())
            c.Execute("CREATE TRIGGER fixture_abort BEFORE UPDATE OF title ON releases WHEN NEW.title LIKE 'Dune%' BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => f.Sync("a", "Dune (2021) 1080p BluRay x264-GRP"));
        Assert.Equal(before, JsonSerializer.Serialize(f.Read("a")));
        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM poster_store_refs"));
        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM release_arr_status"));
    }

    [Fact]
    public void DuplicateGuidChangesInOneBatchDoNotLeakEarlierIdentityFields()
    {
        using var f = new Fixture(); f.Sync("a", "Arrival (2016) 1080p BluRay x264-GRP"); f.Stamp("a", 329865);
        f.Repository.UpsertMany(1, "TEST", [
            new TorznabItem { Guid = "a", Title = "Dune (2021) 1080p BluRay x264-GRP", CategoryId = 2000 },
            new TorznabItem { Guid = "a", Title = "Interstellar 1080p BluRay x264-GRP", CategoryId = 2000 }
        ], categoryMap: new() { [2000] = ("films", "Films") });
        var row = f.Read("a");
        Assert.Null(row.Year); Assert.Null(row.EffectiveTmdbId); Assert.Null(row.EffectivePoster);
        Assert.Contains("interstellar", row.Normalized);
        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM releases"));
    }

    [Fact]
    public void SameTitleWithAnotherExplicitYearReevaluatesTheWork()
    {
        using var f = new Fixture(); f.Sync("a", "Dune (1984) 1080p BluRay x264-GRP"); f.Stamp("a", 841);
        var old = f.Read("a"); f.Sync("a", "Dune (2021) 1080p BluRay x264-GRP");
        Assert.NotEqual(old.EntityId, f.Read("a").EntityId);
        Assert.Null(f.Read("a").EffectiveTmdbId);
        Assert.Equal(2021, f.Read("a").Year);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "feedarr-rss-identity-" + Guid.NewGuid().ToString("N"));
        public readonly Db Db;
        public readonly ReleaseRepository Repository;
        public readonly CaptureLog Log = new();
        public Fixture()
        {
            Db = new Db(OptionsFactory.Create(new AppOptions { DataDir = _dir, DbFileName = "fixture.db" }));
            new MigrationsRunner(Db, NullLogger<MigrationsRunner>.Instance).Run();
            Repository = new ReleaseRepository(Db, new TitleParser(), new UnifiedCategoryResolver(), Log);
            using var c = Db.Open();
            c.Execute("INSERT INTO sources(id,name,enabled,torznab_url,api_key,auth_mode,created_at_ts,updated_at_ts) VALUES(1,'fixture',1,'http://fixture.test','fixture','query',1,1)");
        }
        public int Sync(string guid, string title) => Repository.UpsertMany(1, "TEST", [new TorznabItem { Guid = guid, Title = title, CategoryId = 2000, CategoryIds = [2000] }], createdAtTs: 100, categoryMap: new() { [2000] = ("films", "Films") });
        public Row Read(string guid)
        {
            using var c = Db.Open();
            return c.QuerySingle<Row>("""
                SELECT r.id Id,r.entity_id EntityId,r.title Title,r.year Year,r.title_normalized Normalized,r.dedupe_key Dedupe,
                    r.tmdb_id OwnTmdbId,r.poster_file OwnPoster,
                    COALESCE(me.tmdb_id,r.tmdb_id) EffectiveTmdbId,
                    COALESCE(r.poster_file,me.poster_file) EffectivePoster,
                    COALESCE(me.ext_overview,r.ext_overview) Overview
                FROM releases r LEFT JOIN media_entities me ON me.id=r.entity_id WHERE r.guid=@guid
                """, new { guid });
        }
        public void Stamp(string guid, int tmdb)
        {
            var row = Read(guid);
            using var c = Db.Open();
            c.Execute("""
                UPDATE releases SET tmdb_id=@tmdb,tmdb_media_type='movie',poster_file=@poster,
                    poster_store_dir=@store,poster_key=@store,ext_provider='tmdb',ext_provider_id=CAST(@tmdb AS TEXT),ext_overview=@overview WHERE id=@id;
                UPDATE media_entities SET tmdb_id=@tmdb,tmdb_media_type='movie',poster_file=@poster,
                    ext_provider='tmdb',ext_provider_id=CAST(@tmdb AS TEXT),ext_overview=@overview WHERE id=@entity;
                INSERT OR REPLACE INTO release_arr_status(release_id,in_sonarr,in_radarr,checked_at_ts) VALUES(@id,0,1,1);
                """, new { tmdb, poster = $"poster-{tmdb}.jpg", store = $"tmdb-movie-{tmdb}", overview = $"details-{tmdb}", id = row.Id, entity = row.EntityId });
        }
        public long Scalar(string sql) { using var c = Db.Open(); return c.ExecuteScalar<long>(sql); }
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); }
    }
    private sealed class Row
    {
        public long Id { get; set; } public long EntityId { get; set; }
        public string? Title { get; set; } public int? Year { get; set; }
        public string? Normalized { get; set; } public string? Dedupe { get; set; }
        public int? OwnTmdbId { get; set; } public int? EffectiveTmdbId { get; set; }
        public string? OwnPoster { get; set; } public string? EffectivePoster { get; set; } public string? Overview { get; set; }
    }
    private sealed class CaptureLog : ILogger<ReleaseRepository>
    {
        public readonly List<string> Warnings = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        { if (level == LogLevel.Warning) Warnings.Add(formatter(state, error)); }
    }
}
