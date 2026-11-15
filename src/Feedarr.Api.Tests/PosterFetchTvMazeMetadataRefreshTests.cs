using System.Text.Json;
using Feedarr.Api.Models;

namespace Feedarr.Api.Tests;

public sealed class PosterFetchTvMazeMetadataRefreshTests
{
    [Fact]
    public async Task HistoricalCacheCannotReplaceAnUntypedEstablishedTmdbIdentity()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeHitNoTvdb);
        var id = rig.CreateRelease("Show Tvmaze Hit", "Show Tvmaze Hit", 2015, UnifiedCategory.Serie, "series", tmdbId: 777);
        rig.SeedPosterMatch("series", "Show Tvmaze Hit", 2015, null, null, 3101, null);
        var result = await rig.FetchAsync(id);
        Assert.True(result.Ok);
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(result.Body));
        Assert.Equal("historical_identity_type_required", body.RootElement.GetProperty("refreshUnavailable").GetString());
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Equal(777, row.TmdbId); Assert.Null(row.TmdbMediaType); Assert.Null(row.ExtProvider);
        Assert.Empty(rig.TmdbSearchRequests); Assert.Equal(0, rig.TmdbDetailsCalls);
    }

    [Fact]
    public async Task TvmazeHitPublishesItsOwnVerifiedMetadataWithoutAnImplicitTmdbMapping()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeHit, completeTmdbMetadata: true);
        var id = rig.CreateRelease("Show Tvmaze Hit (2015)", "Show Tvmaze Hit", 2015, UnifiedCategory.Serie, "series");
        Assert.True((await rig.FetchAsync(id)).Ok);
        AssertTvmazeSnapshot(rig, id, "3001");
    }

    [Fact]
    public async Task TvmazeHitReplacesUnverifiedForeignMetadataWithItsOwnSnapshot()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeHit, completeTmdbMetadata: true);
        var old = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 180;
        var id = rig.CreateRelease("Show Tvmaze Hit (2015)", "Show Tvmaze Hit", 2015, UnifiedCategory.Serie, "series",
            extProvider: "tmdb", extOverview: "already present", extUpdatedAtTs: old);
        Assert.True((await rig.FetchAsync(id)).Ok);
        AssertTvmazeSnapshot(rig, id, "3001");
        Assert.NotEqual("already present", rig.GetReleaseForPoster(id)!.ExtOverview);
    }

    [Fact]
    public async Task TvmazeWithoutTvdbStillPublishesAndRefreshesTheSameSeriesIdentity()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeHitNoTvdb, completeTmdbMetadata: true);
        var id = rig.CreateRelease("Show Tvmaze Hit (2015)", "Show Tvmaze Hit", 2015, UnifiedCategory.Serie, "series");
        Assert.True((await rig.FetchAsync(id)).Ok);
        AssertTvmazeSnapshot(rig, id, "3101");
        var before = rig.GetReleaseForPoster(id)!;
        var searches = rig.TmdbSearchRequests.Count;
        Assert.True((await rig.Posters.FetchPosterAsync(id, default, false, skipIfExists: false)).Ok);
        AssertTvmazeSnapshot(rig, id, "3101");
        Assert.NotEqual(before.PosterFile, rig.GetReleaseForPoster(id)!.PosterFile);
        Assert.Equal(searches, rig.TmdbSearchRequests.Count);
    }

    private static void AssertTvmazeSnapshot(PosterMatchingContractTestRig rig, long id, string identity)
    {
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Equal("tvmaze", row.ExtProvider); Assert.Equal(identity, row.ExtProviderId);
        Assert.Equal(1, row.ExtSnapshotVersion); Assert.True(row.ExtUpdatedAtTs > 0);
        Assert.Null(row.TmdbId); Assert.Equal("Show Tvmaze Hit", row.ExtTitle);
        Assert.NotNull(row.PosterFile);
        Assert.True(File.Exists(Path.Combine(rig.Posters.PostersDirPath, row.PosterFile!)));
        Assert.Equal(0, rig.TmdbDetailsCalls);
    }
}
