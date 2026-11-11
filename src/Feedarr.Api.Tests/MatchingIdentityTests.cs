using System.Reflection;
using Dapper;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Tests;

public sealed partial class PosterFetchStoreMaterializationTests
{
    [Fact]
    public void TmdbIdentity_SameNumberNeverOverlapsAcrossTypesOrWithUntypedHistory()
    {
        Assert.NotEqual(new TmdbIdentity("movie", 123).Key, new TmdbIdentity("tv", 123).Key);
        var movie = new PosterMatchIds(123, null, null, null, null, "movie");
        Assert.False(movie.Overlaps(new(123, null, null, null, null, "series")));
        Assert.False(movie.Overlaps(new(123, null, null, null, null)));
        Assert.True(movie.Overlaps(new(123, null, null, null, null, "film")));
        Assert.True(new PosterMatchIds(null, 42, null, null, null).Overlaps(new(null, 42, null, null, null)));
    }

    [Fact]
    public void ManualSearchDedup_SameNumericIdRetainsMovieAndSeries()
    {
        var controller = typeof(Feedarr.Api.Controllers.PostersController);
        var resultType = controller.GetNestedType("PosterSearchResult", BindingFlags.NonPublic)!;
        object Result(string mediaType)
        {
            var value = Activator.CreateInstance(resultType)!;
            resultType.GetProperty("Provider")!.SetValue(value, "tmdb");
            resultType.GetProperty("TmdbId")!.SetValue(value, 123);
            resultType.GetProperty("MediaType")!.SetValue(value, mediaType);
            return value;
        }
        var key = controller.GetMethod("BuildSearchDedupKey", BindingFlags.Static | BindingFlags.NonPublic)!;
        var movie = key.Invoke(null, new[] { Result("movie") });
        var tv = key.Invoke(null, new[] { Result("series") });
        Assert.NotEqual(movie, tv);
        Assert.Equal(tv, key.Invoke(null, new[] { Result("tv") }));
    }

    [Fact]
    public void TmdbCandidates_DeduplicateWithinTypeOnly()
    {
        var method = typeof(PosterFetchService).GetMethod("DedupCandidates", BindingFlags.NonPublic | BindingFlags.Static)!;
        var movie = new TmdbClient.SearchResult(123, "Same", null, "/movie.jpg", "movie", 2024, "en");
        var tv = movie with { MediaType = "series", PosterPath = "/tv.jpg" };
        var result = (List<TmdbClient.SearchResult>)method.Invoke(null, new object[] { new[] { movie, tv, movie, tv } })!;
        Assert.Equal(new[] { movie, tv }, result);
    }

    [Fact]
    public void MatchCaches_AreDistinctByType_AndLegacyDoesNotAuthorizeIdOverlap()
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        c.SeedPosterMatch("movie", "Same", 2024, "tmdb-movie-123-w500.jpg", 123, "tmdb", "tmdb", "movie:123");
        c.SeedPosterMatch("series", "Same", 2024, "tmdb-tv-123-w500.jpg", 123, "tmdb", "tmdb", "tv:123");
        var cache = new PosterMatchCacheService(c.Db);
        var movie = cache.TryGet(PosterMatchCacheService.BuildFingerprint(new("movie", "same", 2024, null, null)));
        var tv = cache.TryGet(PosterMatchCacheService.BuildFingerprint(new("series", "same", 2024, null, null)));
        Assert.NotNull(movie); Assert.NotNull(tv);
        Assert.NotEqual(movie.Fingerprint, tv.Fingerprint);
        Assert.NotEqual(movie.PosterFile, tv.PosterFile);
    }

    [Fact]
    public async Task ManualTmdb_SameNumberUsesSeparateAssociationsFilesAndStores()
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var movie = c.CreateRelease("Same", "Same", 2024);
        var tv = c.CreateRelease("Same Series", "Same", 2024);
        using (var conn = c.Db.Open()) conn.Execute("UPDATE releases SET unified_category='Serie',media_type='series' WHERE id=@tv", new { tv });
        Assert.NotNull(await c.Posters.SaveTmdbPosterAsync(movie, 100, "/movie.jpg", default, mediaType: "movie"));
        Assert.NotNull(await c.Posters.SaveTmdbPosterAsync(tv, 100, "/tv.jpg", default, mediaType: "series"));
        var m = c.Releases.GetForPoster(movie)!; var t = c.Releases.GetForPoster(tv)!;
        Assert.NotEqual(m.EntityId, t.EntityId);
        Assert.Equal("tmdb:movie:100", m.PosterKey);
        Assert.Equal("tmdb:tv:100", t.PosterKey);
        Assert.NotEqual(m.PosterFile, t.PosterFile);
        Assert.NotEqual(m.PosterStoreDir, t.PosterStoreDir);
        Assert.True(File.Exists(Path.Combine(c.StoreDir, m.PosterStoreDir!, "original.jpg")));
        Assert.True(File.Exists(Path.Combine(c.StoreDir, t.PosterStoreDir!, "original.jpg")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManualTmdb_NormalAndForcedRefreshIgnoreSurvivingWrongCache(bool skipIfExists)
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var id = c.CreateRelease("The Matrix", "The Matrix", 1999);
        await c.Posters.SaveTmdbPosterAsync(id, 200, "/correct.jpg", default);
        var before = c.Releases.GetForPoster(id)!;
        c.WriteLegacyPoster("tmdb-movie-100-w500.jpg", PosterStoreContext.CreateValidImageBytes());
        c.SeedPosterMatch("movie", "The Matrix", 1999, "tmdb-movie-100-w500.jpg", 100, "tmdb", "tmdb", "movie:100");
        var result = await c.Posters.FetchPosterAsync(id, default, false, skipIfExists);
        Assert.True(result.Ok);
        var after = c.Releases.GetForPoster(id)!;
        Assert.Equal(200, after.TmdbId);
        Assert.Equal(before.PosterFile, after.PosterFile);
        Assert.Equal(before.MatchRevision, after.MatchRevision);
        Assert.Equal("200", after.ManualMatchId);
    }

    [Fact]
    public async Task InFlightAutomaticWorker_CannotPublishAfterManualCorrection()
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var id = c.CreateRelease("The Matrix", "The Matrix", 1999);
        c.Releases.SetManualMatch(id, "tmdb", "movie", "100");
        c.Releases.ClearManualMatch(id); // Same entity, different revision: exercise more than a rebind check.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        c.TmdbHandler.BeforeMovieSearch = async () => { entered.SetResult(); await resume.Task; };
        var worker = c.Posters.FetchPosterAsync(id, default, false, false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await c.Posters.SaveTmdbPosterAsync(id, 200, "/correct.jpg", default);
        var corrected = c.Releases.GetForPoster(id)!;
        resume.SetResult();
        var result = await worker;
        Assert.Equal(409, result.StatusCode);
        var after = c.Releases.GetForPoster(id)!;
        Assert.Equal(200, after.TmdbId);
        Assert.Equal(corrected.PosterFile, after.PosterFile);
        Assert.Equal(corrected.MatchRevision, after.MatchRevision);
    }

    [Fact]
    public async Task SharedEntity_ManualCorrectionUpdatesAllReleaseIdentitiesAndClearsOldMetadata()
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var first = c.CreateRelease("Shared", "Shared", 2024, tmdbId: 100, extProvider: "tmdb", extOverview: "wrong metadata");
        var second = c.CreateRelease("Shared Other", "Shared", 2024, tmdbId: 100, extProvider: "tmdb", extOverview: "wrong metadata");
        c.Releases.SetManualMatch(first, "tmdb", "movie", "100");
        using (var conn = c.Db.Open()) conn.Execute("UPDATE releases SET entity_id=@entity WHERE id=@second", new { entity = c.Releases.GetForPoster(first)!.EntityId, second });
        await c.Posters.SaveTmdbPosterAsync(first, 200, "/correct.jpg", default);
        foreach (var id in new[] { first, second })
        {
            var r = c.Releases.GetForPoster(id)!;
            Assert.Equal(200, r.TmdbId);
            Assert.Equal("200", r.ManualMatchId);
            Assert.NotEqual("wrong metadata", r.ExtOverview);
            Assert.Equal(c.Releases.GetForPoster(first)!.PosterFile, r.PosterFile);
        }
        using var db = c.Db.Open();
        Assert.Equal(2, db.ExecuteScalar<int>("SELECT count(*) FROM releases WHERE entity_id=@entity AND tmdb_id=200", new { entity = c.Releases.GetForPoster(first)!.EntityId }));
    }

    [Fact]
    public async Task ManualChangeAndExplicitClear_InvalidatePreviouslyCapturedWorkerRevision()
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var id = c.CreateRelease("The Matrix", "The Matrix", 1999);
        await c.Posters.SaveTmdbPosterAsync(id, 200, "/first.jpg", default);
        var old = c.Releases.GetForPoster(id)!;
        await c.Posters.SaveTmdbPosterAsync(id, 300, "/second.jpg", default);
        Assert.Equal(300, c.Releases.GetForPoster(id)!.TmdbId);
        Assert.True(c.Releases.ClearManualMatch(id));
        using (c.Releases.BeginMatching(old))
            Assert.Throws<ReleaseRepository.StaleMatchException>(() => c.Releases.SaveTmdbId(id, 200));
        Assert.Null(c.Releases.GetForPoster(id)!.ManualMatchProvider);
        Assert.True((await c.Posters.FetchPosterAsync(id, default, false, false)).Ok);
        Assert.Equal(100, c.Releases.GetForPoster(id)!.TmdbId);
    }

    [Fact]
    public async Task ManualMatch_AllowsEnrichmentOfLockedIdentity()
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var id = c.CreateRelease("The Matrix", "The Matrix", 1999);
        await c.Posters.SaveTmdbPosterAsync(id, 100, "/manual.jpg", default);
        var calls = c.TmdbHandler.MovieDetailsCalls;
        Assert.True((await c.Posters.FetchPosterAsync(id, default, false, false)).Ok);
        var after = c.Releases.GetForPoster(id)!;
        Assert.Equal(100, after.TmdbId);
        Assert.Equal("Mock overview", after.ExtOverview);
        Assert.True(c.TmdbHandler.MovieDetailsCalls > calls);
    }

    [Fact]
    public async Task ManualTypeChange_RejectsMetadataFromSameNumberInOldNamespace()
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var id = c.CreateRelease("The Matrix", "The Matrix", 1999);
        await c.Posters.SaveTmdbPosterAsync(id, 100, "/movie.jpg", default, mediaType: "movie");
        var old = c.Releases.GetForPoster(id)!;
        await c.Posters.SaveTmdbPosterAsync(id, 100, "/tv.jpg", default, mediaType: "tv");
        using (c.Releases.BeginMatching(old))
            Assert.Throws<ReleaseRepository.StaleMatchException>(() => c.Releases.UpdateExternalDetails(id, "tmdb", "100", "Wrong movie", "Wrong overview", null, null, null, null, null, null, null, null, null));
        var current = c.Releases.GetForPoster(id)!;
        Assert.Equal("tv", current.TmdbMediaType);
        Assert.Equal("series", current.MediaType);
        Assert.NotEqual("Wrong overview", current.ExtOverview);
    }

    [Fact]
    public async Task RenameCorrection_DropsOldIdentityAndRejectsOldWorker()
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var id = c.CreateRelease("Wrong title", "Wrong title", 1999);
        await c.Posters.SaveTmdbPosterAsync(id, 200, "/wrong.jpg", default);
        var old = c.Releases.GetForPoster(id)!;
        c.Releases.RenameAndRebindEntity(id, "The Matrix 1999");
        using (c.Releases.BeginMatching(old))
            Assert.Throws<ReleaseRepository.StaleMatchException>(() => c.Releases.SavePoster(id, 200, "/wrong.jpg", "wrong.jpg"));
        Assert.Null(c.Releases.GetForPoster(id)!.TmdbId);
        Assert.Null(c.Releases.GetForPoster(id)!.PosterFile);
        Assert.True((await c.Posters.FetchPosterAsync(id, default, false, false)).Ok);
        Assert.Equal(100, c.Releases.GetForPoster(id)!.TmdbId);
    }

    [Fact]
    public async Task HistoricalManualEvidence_IsPreservedWithoutGuessingOrDownloading()
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var id = c.CreateRelease("Ambiguous", "Ambiguous", 2024, posterFile: "tmdb-123-manual.jpg", tmdbId: 123);
        var bytes = PosterStoreContext.CreateValidImageBytes(); c.WriteLegacyPoster("tmdb-123-manual.jpg", bytes);
        Assert.True((await c.Posters.FetchPosterAsync(id, default, false, false)).Ok);
        Assert.Equal(123, c.Releases.GetForPoster(id)!.TmdbId);
        Assert.Null(c.Releases.GetForPoster(id)!.TmdbMediaType);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(c.PostersDir, "tmdb-123-manual.jpg")));
        Assert.Equal(0, c.TmdbHandler.MovieDetailsCalls);
    }
    [Fact]
    public void Migration0057_PreservesAmbiguousHistoryAndCanRestartWithoutDataMutation()
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var id = c.CreateRelease("Historical", "Historical", 1999, posterFile: "tmdb-100-w500.jpg", posterProvider: "tmdb", posterProviderId: "100", tmdbId: 100, extProvider: "tmdb", extOverview: "historic overview");
        var bytes = PosterStoreContext.CreateValidImageBytes();
        c.WriteLegacyPoster("tmdb-100-w500.jpg", bytes);
        c.SeedPosterMatch("movie", "Historical", 1999, "tmdb-100-w500.jpg", 100, "tmdb", "tmdb", "100");
        using (var conn = c.Db.Open())
        {
            conn.Execute("INSERT INTO media_entities(unified_category,title_clean,year,created_at_ts,updated_at_ts,tmdb_id,poster_file,poster_store_dir) VALUES('Film','Historical',1999,1,1,100,'tmdb-100-w500.jpg','tmdb-100')");
            conn.Execute("UPDATE releases SET entity_id=last_insert_rowid(),poster_store_dir='tmdb-100' WHERE id=@id", new { id });
            foreach (var column in new[] { "tmdb_media_type", "manual_match_provider", "manual_match_type", "manual_match_id", "match_revision" })
                conn.Execute($"ALTER TABLE media_entities DROP COLUMN {column}");
            conn.Execute("ALTER TABLE releases DROP COLUMN tmdb_media_type; DELETE FROM schema_migrations WHERE id='0057_matching_identity.sql'");
        }
        var runner = new Feedarr.Api.Data.MigrationsRunner(c.Db, Microsoft.Extensions.Logging.Abstractions.NullLogger<Feedarr.Api.Data.MigrationsRunner>.Instance);
        runner.Run(); runner.Run();
        var row = c.Releases.GetForPoster(id)!;
        Assert.Equal(100, row.TmdbId); Assert.Null(row.TmdbMediaType); Assert.Null(row.ManualMatchProvider);
        Assert.Equal("tmdb-100-w500.jpg", row.PosterFile); Assert.Equal("tmdb-100", row.PosterStoreDir);
        Assert.Equal("historic overview", row.ExtOverview);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(c.PostersDir, row.PosterFile!)));
        using var db = c.Db.Open();
        Assert.Equal(1, db.ExecuteScalar<int>("SELECT count(*) FROM poster_matches"));
        Assert.Equal(1, db.ExecuteScalar<int>("SELECT count(*) FROM schema_migrations WHERE id='0057_matching_identity.sql'"));
        Assert.Equal("ok", db.ExecuteScalar<string>("PRAGMA integrity_check"));
        Assert.Empty(db.Query("PRAGMA foreign_key_check"));
        Assert.Equal(0, c.TmdbHandler.MovieDetailsCalls);
    }

    [Fact]
    public async Task InvalidManualImage_DoesNotErasePreviousIdentity()
    {
        using var c = new PosterStoreContext(ImagePayloadMode.InvalidImage);
        var id = c.CreateRelease("Historical", "Historical", 1999);
        c.Releases.SetManualMatch(id, "tmdb", "movie", "100");
        c.Releases.SavePoster(id, 100, "/valid.jpg", "previous.jpg", "movie");
        var before = c.Releases.GetForPoster(id)!;
        await Assert.ThrowsAsync<InvalidDataException>(() => c.Posters.SaveTmdbPosterAsync(id, 200, "/invalid.jpg", default));
        var after = c.Releases.GetForPoster(id)!;
        Assert.Equal(before.TmdbId, after.TmdbId);
        Assert.Equal(before.PosterFile, after.PosterFile);
        Assert.Equal(before.MatchRevision, after.MatchRevision);
    }

    [Fact]
    public async Task HistoricallyMixedEntity_IsProtectedFromAutomaticOverwrite()
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var movie = c.CreateRelease("The Matrix", "The Matrix", 1999);
        var tv = c.CreateRelease("The Matrix TV", "The Matrix", 1999);
        using (var conn = c.Db.Open())
        {
            conn.Execute("INSERT INTO media_entities(unified_category,title_clean,year,created_at_ts,updated_at_ts,tmdb_id) VALUES('Film','The Matrix',1999,1,1,200)");
            var entity = conn.ExecuteScalar<long>("SELECT last_insert_rowid()");
            conn.Execute("UPDATE releases SET entity_id=@entity WHERE id IN (@movie,@tv); UPDATE releases SET media_type='series' WHERE id=@tv", new { entity, movie, tv });
        }
        var result = await c.Posters.FetchPosterAsync(movie, default, false, false);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal(200, c.Releases.GetForPoster(movie)!.TmdbId);
        Assert.Equal(c.Releases.GetForPoster(movie)!.EntityId, c.Releases.GetForPoster(tv)!.EntityId);
    }

    [Fact]
    public async Task ManualMovieCorrection_InvalidatesOnlyRelatedCacheType()
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var id = c.CreateRelease("Same", "Same", 2024);
        c.SeedPosterMatch("movie", "Same", 2024, "tmdb-movie-100-w500.jpg", 100, "tmdb", "tmdb", "movie:100");
        c.SeedPosterMatch("series", "Same", 2024, "tmdb-tv-100-w500.jpg", 100, "tmdb", "tmdb", "tv:100");
        await c.Posters.SaveTmdbPosterAsync(id, 200, "/correct.jpg", default);
        var cache = new PosterMatchCacheService(c.Db);
        Assert.Null(cache.TryGet(PosterMatchCacheService.BuildFingerprint(new("movie", "same", 2024, null, null))));
        Assert.NotNull(cache.TryGet(PosterMatchCacheService.BuildFingerprint(new("series", "same", 2024, null, null))));
    }

}
