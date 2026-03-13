using Feedarr.Api.Models;

namespace Feedarr.Api.Tests;

public sealed class PosterFetchTvMazeMetadataRefreshTests
{
    [Fact]
    public async Task FetchPosterAsync_TvmazeCacheHit_WithEffectiveTmdbId_RefreshesMissingMetadata()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeHitNoTvdb);
        var releaseId = rig.CreateRelease(
            title: "Show Tvmaze Hit",
            titleClean: "Show Tvmaze Hit",
            year: 2015,
            unifiedCategory: UnifiedCategory.Serie,
            mediaType: "series",
            tmdbId: 777);

        rig.SeedPosterMatch(
            mediaType: "series",
            titleClean: "Show Tvmaze Hit",
            year: 2015,
            tmdbId: null,
            tvdbId: null,
            tvmazeId: 3101,
            posterFile: null);

        var result = await rig.FetchAsync(releaseId);

        Assert.True(result.Ok);
        var release = rig.GetReleaseForPoster(releaseId);
        Assert.NotNull(release);
        Assert.True((release!.ExtUpdatedAtTs ?? 0) > 0);
        Assert.Equal("tmdb", release.ExtProvider);
        Assert.Equal(1, rig.TmdbDetailsCalls);
    }

    [Fact]
    public async Task FetchPosterAsync_TvmazeDirectHit_WithTmdbId_RefreshesMissingMetadata()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeHit);
        var releaseId = rig.CreateRelease(
            title: "Show Tvmaze Hit",
            titleClean: "Show Tvmaze Hit",
            year: 2015,
            unifiedCategory: UnifiedCategory.Serie,
            mediaType: "series");

        var result = await rig.FetchAsync(releaseId);

        Assert.True(result.Ok);
        var release = rig.GetReleaseForPoster(releaseId);
        Assert.NotNull(release);
        Assert.True((release!.ExtUpdatedAtTs ?? 0) > 0);
        Assert.Equal("tmdb", release.ExtProvider);
        Assert.Equal(1, rig.TmdbDetailsCalls);
    }

    [Fact]
    public async Task FetchPosterAsync_TvmazeDirectHit_WithExistingMetadata_DoesNotRefreshMetadata()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeHit);
        var existingTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 180;
        var releaseId = rig.CreateRelease(
            title: "Show Tvmaze Hit",
            titleClean: "Show Tvmaze Hit",
            year: 2015,
            unifiedCategory: UnifiedCategory.Serie,
            mediaType: "series",
            extProvider: "tmdb",
            extOverview: "already present",
            extUpdatedAtTs: existingTs);

        var result = await rig.FetchAsync(releaseId);

        Assert.True(result.Ok);
        var release = rig.GetReleaseForPoster(releaseId);
        Assert.NotNull(release);
        Assert.Equal(existingTs, release!.ExtUpdatedAtTs);
        Assert.Equal("already present", release.ExtOverview);
        Assert.Equal(0, rig.TmdbDetailsCalls);
    }

    [Fact]
    public async Task FetchPosterAsync_TvmazeDirectHit_WithoutEffectiveTmdbId_DoesNotRefreshMetadata()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeHitNoTvdb);
        var releaseId = rig.CreateRelease(
            title: "Show Tvmaze Hit",
            titleClean: "Show Tvmaze Hit",
            year: 2015,
            unifiedCategory: UnifiedCategory.Serie,
            mediaType: "series");

        var result = await rig.FetchAsync(releaseId);

        Assert.True(result.Ok);
        var release = rig.GetReleaseForPoster(releaseId);
        Assert.NotNull(release);
        Assert.Null(release!.ExtUpdatedAtTs);
        Assert.Equal(0, rig.TmdbDetailsCalls);
    }
}
