using System.Text.Json;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Posters;
using Microsoft.Extensions.Logging.Abstractions;

namespace Feedarr.Api.Tests;

public sealed partial class PosterFetchStoreMaterializationTests
{
    [Fact]
    public async Task Matching_HighScoreWrongYear_DoesNotAssociateOrDownloadPoster()
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var id = c.CreateRelease("The Matrix", "The Matrix", 2012);
        var result = await c.Posters.FetchPosterAsync(id, default, true, false);
        Assert.False(result.Ok);
        Assert.Null(c.Releases.GetForPoster(id)!.TmdbId);
        Assert.Null(c.Releases.GetForPoster(id)!.PosterFile);
        Assert.Equal(0, c.TmdbHandler.MovieDetailsCalls);
    }

    [Fact]
    public async Task Matching_RejectsFirstCandidateAndSelectsConsistentAlternative()
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var id = c.CreateRelease("The Matrix", "The Matrix", 2012);
        c.TmdbHandler.MovieSearchJson = """{"results":[{"id":999,"title":"The Matrix","poster_path":"/wrong.jpg","release_date":"1990-01-01"},{"id":100,"title":"The Matrix","poster_path":"/correct.jpg","release_date":"2012-01-01"}]}""";
        var result = await c.Posters.FetchPosterAsync(id, default, true, false);
        Assert.True(result.Ok, JsonSerializer.Serialize(result.Body));
        Assert.Equal(100, c.Releases.GetForPoster(id)!.TmdbId);
    }

    [Fact]
    public async Task Matching_IdsFollowSelectedPosterRatherThanDifferentCandidateWithoutImage()
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var id = c.CreateRelease("The Matrix", "The Matrix", 1999);
        c.TmdbHandler.MovieSearchJson = """{"results":[{"id":999,"title":"The Matrix","poster_path":null,"release_date":"1999-01-01"},{"id":100,"title":"The Matrix","poster_path":"/correct.jpg","release_date":"1999-01-01"}]}""";
        Assert.True((await c.Posters.FetchPosterAsync(id, default, false, false)).Ok);
        var row = c.Releases.GetForPoster(id)!;
        Assert.Equal(100, row.TmdbId); Assert.Equal("100", row.ExtProviderId);
        Assert.Equal("tmdb:movie:100", row.PosterKey);
    }

    [Theory]
    [InlineData("tmdb", "200", "movie")]
    [InlineData("rawg", "222", null)]
    [InlineData("igdb", "333", null)]
    [InlineData("tmdb", "100", "tv")]
    public void Metadata_IdentityChangeReplacesMissingFieldsAndInvalidatesOldAssets(string provider, string externalId, string? type)
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var id = c.CreateRelease("User title", "User title", 1999);
        c.Releases.SetManualMatch(id, "tmdb", "movie", "100"); c.Releases.ClearManualMatch(id);
        c.Releases.UpdateExternalDetails(id, "tmdb", "100", "Old", "Old overview", "Old tagline", "Old genres", "1999-01-01", 120, 8.5, 400, "Old directors", "Old writers", "Old cast", "movie");
        c.Releases.SavePoster(id, 100, "/old.jpg", "tmdb-movie-100-w500.jpg", "movie");
        c.Releases.SavePosterWithStore(id, 100, "/old.jpg", "tmdb-movie-100-w500.jpg", "tmdb:movie:100", "tmdb-movie-100", "movie");
        c.Releases.UpdatePosterAttemptSuccess(id, "tmdb", "100", null, null, null);
        c.WriteLegacyPoster("tmdb-movie-100-w500.jpg", PosterStoreContext.CreateValidImageBytes());
        var peer = c.CreateRelease("Other release", "User title", 1999);
        var entity = c.Releases.GetForPoster(id)!.EntityId;
        using (var db = c.Db.Open())
            db.Execute("UPDATE releases SET entity_id=@entity WHERE id=@peer; UPDATE releases SET seen=1,title_manual_override=1 WHERE id=@id; UPDATE media_entities SET tvdb_id=999,request_tmdb_id=888 WHERE id=@entity", new { id, peer, entity });
        c.Releases.UpdateExternalDetails(id, provider, externalId, "New work", null, null, null, null, null, null, null, null, null, null, type);
        foreach (var release in new[] { id, peer })
        {
            var row = c.Releases.GetForPoster(release)!;
            Assert.Equal(provider, row.ExtProvider); Assert.Equal(externalId, row.ExtProviderId);
            Assert.Equal("New work", row.ExtTitle);
            Assert.Null(row.ExtOverview); Assert.Null(row.ExtTagline); Assert.Null(row.ExtGenres);
            Assert.Null(row.ExtReleaseDate); Assert.Null(row.ExtRuntimeMinutes); Assert.Null(row.ExtRating); Assert.Null(row.ExtVotes);
            Assert.Null(row.ExtDirectors); Assert.Null(row.ExtWriters); Assert.Null(row.ExtCast);
            Assert.Null(row.PosterFile); Assert.Null(row.PosterStoreDir); Assert.Null(row.TvdbId);
            Assert.Equal(provider == "tmdb" ? int.Parse(externalId) : (int?)null, row.TmdbId);
        }
        Assert.True(File.Exists(Path.Combine(c.PostersDir, "tmdb-movie-100-w500.jpg")));
        using var conn = c.Db.Open();
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT seen FROM releases WHERE id=@id", new { id }));
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT title_manual_override FROM releases WHERE id=@id", new { id }));
        Assert.Equal("User title", conn.ExecuteScalar<string>("SELECT title FROM releases WHERE id=@id", new { id }));
        Assert.Null(conn.ExecuteScalar<int?>("SELECT request_tmdb_id FROM media_entities WHERE id=@entity", new { entity }));
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT count(*) FROM poster_store_refs WHERE release_id=@id", new { id }));
    }

    [Fact]
    public void Metadata_SameIdentitySnapshotRepairsHistoricalHybridInsteadOfCoalescing()
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var id = c.CreateRelease("Game", "Game", 2024, extProvider: "rawg", extOverview: "Old TMDB overview", extUpdatedAtTs: 1);
        using (var db = c.Db.Open()) db.Execute("UPDATE releases SET ext_provider_id='222',ext_tagline='Old tagline',ext_snapshot_version=NULL WHERE id=@id", new { id });
        c.Releases.UpdateExternalDetails(id, "rawg", "222", "Game", null, null, null, null, null, null, null, null, null, null);
        var row = c.Releases.GetForPoster(id)!;
        Assert.Null(row.ExtOverview); Assert.Null(row.ExtTagline); Assert.Equal(1, row.ExtSnapshotVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Metadata_InFlightResultCannotOverwriteReplacementIdentity(bool manual)
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var id = c.CreateRelease("The Matrix", "The Matrix", 1999);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        c.TmdbHandler.BeforeMovieDetails = async () => { entered.SetResult(); await resume.Task; };
        var old = c.Posters.FetchPosterAsync(id, default, false, false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (manual) await c.Posters.SaveTmdbPosterAsync(id, 200, "/manual.jpg", default);
        else c.Releases.UpdateExternalDetails(id, "tmdb", "200", "Replacement", null, null, null, null, null, null, null, null, null, null, "movie");
        resume.SetResult();
        Assert.Equal(409, (await old).StatusCode);
        var row = c.Releases.GetForPoster(id)!;
        Assert.Equal(200, row.TmdbId); Assert.Null(row.ExtOverview);
        if (manual) Assert.Equal("200", row.ManualMatchId);
        else Assert.Equal("Replacement", row.ExtTitle);
    }

    [Fact]
    public void Migration0058_IsAdditive_AndHybridRepairIsLazy()
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var id = c.CreateRelease("Legacy", "Legacy", 2024, posterFile: "unknown-manual.jpg", extProvider: "rawg", extOverview: "Hybrid legacy metadata", extUpdatedAtTs: 1);
        var bytes = PosterStoreContext.CreateValidImageBytes(); c.WriteLegacyPoster("unknown-manual.jpg", bytes);
        using (var db = c.Db.Open())
        {
            db.Execute("UPDATE releases SET ext_provider_id='222' WHERE id=@id", new { id });
            db.Execute("ALTER TABLE media_entities DROP COLUMN ext_snapshot_version; ALTER TABLE releases DROP COLUMN ext_snapshot_version; DELETE FROM schema_migrations WHERE id='0058_metadata_snapshot_provenance.sql'");
        }
        var runner = new MigrationsRunner(c.Db, NullLogger<MigrationsRunner>.Instance); runner.Run(); runner.Run();
        var row = c.Releases.GetForPoster(id)!;
        Assert.Equal("Hybrid legacy metadata", row.ExtOverview); Assert.Null(row.ExtSnapshotVersion);
        Assert.Equal("unknown-manual.jpg", row.PosterFile); Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(c.PostersDir, row.PosterFile!)));
        c.Releases.UpdateExternalDetails(id, "rawg", "222", "Legacy", null, null, null, null, null, null, null, null, null, null);
        Assert.Null(c.Releases.GetForPoster(id)!.ExtOverview); Assert.Equal(1, c.Releases.GetForPoster(id)!.ExtSnapshotVersion);
        using var conn = c.Db.Open(); Assert.Equal("ok", conn.ExecuteScalar<string>("PRAGMA integrity_check")); Assert.Empty(conn.Query("PRAGMA foreign_key_check"));
        Assert.Equal(0, c.TmdbHandler.MovieDetailsCalls);
    }
    [Fact]
    public void Metadata_ManualIdentityCannotBeReplacedByAnotherProvider()
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var id = c.CreateRelease("User corrected title", "User corrected title", 2024);
        c.Releases.SetManualMatch(id, "tmdb", "movie", "100");
        c.Releases.UpdateExternalDetails(id, "tmdb", "100", "Correct", null, null, null, null, null, null, null, null, null, null, "movie");
        Assert.Throws<Feedarr.Api.Data.Repositories.ReleaseRepository.StaleMatchException>(() =>
            c.Releases.UpdateExternalDetails(id, "rawg", "222", "Wrong", null, null, null, null, null, null, null, null, null, null));
        Assert.Equal("100", c.Releases.GetForPoster(id)!.ManualMatchId);
        Assert.Equal("Correct", c.Releases.GetForPoster(id)!.ExtTitle);
    }

    [Fact]
    public async Task Matching_MovieCannotAcceptTvCandidateWithSameTitleAndYear()
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var id = c.CreateRelease("The Matrix", "The Matrix", 1999);
        c.TmdbHandler.MovieSearchJson = """{"results":[]}""";
        c.TmdbHandler.TvSearchJson = """{"results":[{"id":100,"name":"The Matrix","poster_path":"/wrong.jpg","first_air_date":"1999-01-01"}]}""";
        Assert.False((await c.Posters.FetchPosterAsync(id, default, true, false)).Ok);
        Assert.Null(c.Releases.GetForPoster(id)!.TmdbId);
    }

    [Fact]
    public async Task Matching_AdjacentInternationalReleaseAndUnknownYearStillMatch()
    {
        foreach (var year in new int?[] { 1998, 2000, null })
        {
            using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
            var id = c.CreateRelease("The Matrix", "The Matrix", year);
            Assert.True((await c.Posters.FetchPosterAsync(id, default, false, false)).Ok);
            Assert.Equal(100, c.Releases.GetForPoster(id)!.TmdbId);
        }
    }

    [Fact]
    public async Task Metadata_IgdbControllerRejectsResultComputedBeforeManualCorrection()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit);
        var id = rig.CreateRelease("Game Hit", "Game Hit", 2022, UnifiedCategory.JeuWindows, "game");
        Assert.True((await rig.FetchAsync(id)).Ok);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.BeforeIgdbDetails(async () => { entered.SetResult(); await resume.Task; });
        var old = rig.FetchIgdbDetailsAsync(id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Repository.SetManualMatch(id, "igdb", "game", "902");
        resume.SetResult();
        Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(await old);
        Assert.Equal("902", rig.GetReleaseForPoster(id)!.ManualMatchId);
        Assert.Null(rig.GetReleaseForPoster(id)!.ExtOverview);
    }

    [Fact]
    public async Task Metadata_VerifiedTmdbSnapshotSurvivesMappedTvMazePosterRefresh()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeHit);
        var id = rig.CreateRelease("Show Tvmaze Hit", "Show Tvmaze Hit", 2015, UnifiedCategory.Serie, "series", tmdbId: 501);
        rig.Repository.UpdateExternalDetails(id, "tmdb", "501", "Show Tvmaze Hit", "Verified overview", null, null, "2015-01-01", null, null, null, null, null, null, "tv");
        Assert.True((await rig.FetchAsync(id)).Ok);
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Equal("Verified overview", row.ExtOverview);
        Assert.Equal(501, row.TmdbId); Assert.Equal("tvmaze", row.PosterProvider);
        Assert.Equal(0, rig.TmdbDetailsCalls);
    }

    [Fact]
    public async Task Matching_LegacyPolicyCacheCannotReintroduceAnUnverifiedAssociation()
    {
        using var c = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var id = c.CreateRelease("The Matrix", "The Matrix", null);
        c.WriteLegacyPoster("tmdb-movie-999-w500.jpg", PosterStoreContext.CreateValidImageBytes());
        c.SeedPosterMatch("movie", "The Matrix", 1999, "tmdb-movie-999-w500.jpg", 999, "tmdb", "tmdb", "movie:999");
        var oldKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("tmdb-v2:movie|the matrix|1999|null|null"))).ToLowerInvariant();
        using (var db = c.Db.Open()) db.Execute("UPDATE poster_matches SET fingerprint=@oldKey", new { oldKey });
        Assert.True((await c.Posters.FetchPosterAsync(id, default, false, true)).Ok);
        Assert.Equal(100, c.Releases.GetForPoster(id)!.TmdbId);
        Assert.True(File.Exists(Path.Combine(c.PostersDir, "tmdb-movie-999-w500.jpg")));
    }

}
