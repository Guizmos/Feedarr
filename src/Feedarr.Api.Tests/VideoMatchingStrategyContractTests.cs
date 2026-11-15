using System.Text.Json;
using Feedarr.Api.Models;

namespace Feedarr.Api.Tests;

public sealed class VideoMatchingStrategyContractTests
{
    [Fact]
    public async Task TvmazeOnlyCandidatePublishesItsVerifiedSeriesAtomically()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeHit, completeTmdbMetadata: true);
        var id = rig.IngestRelease("Show.Tvmaze.Hit.S01E02.1080p.WEB.x264-GRP", UnifiedCategory.Serie);
        var result = await rig.FetchAsync(id);
        Assert.True(result.Ok, JsonSerializer.Serialize(result.Body));
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Equal("tvmaze", row.ExtProvider); Assert.Equal("3001", row.ExtProviderId);
        Assert.Null(row.TmdbId); // No unverified /find mapping after acceptance.
        Assert.Contains("tvmaze-tv-3001-v2-", row.PosterFile!);
        Assert.Equal("Show Tvmaze Hit", row.ExtTitle);
        var searches = rig.TmdbSearchRequests.Count;
        Assert.True((await rig.FetchAsync(id)).Ok);
        Assert.Equal(searches, rig.TmdbSearchRequests.Count);
        Assert.Equal(row.PosterFile, rig.GetReleaseForPoster(id)!.PosterFile);
    }

    [Fact]
    public async Task TmdbSeriesPublishesLocalizedMetadataWithTypedIdentity()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeMissTmdb, completeTmdbMetadata: true,
            tmdbResponse: (request, _) => {
                Assert.DoesNotContain("/movie/", request.RequestUri!.AbsolutePath);
                return Task.FromResult<System.Net.Http.HttpResponseMessage?>(null);
            });
        var id = rig.IngestRelease("Show.Tmdb.Hit.S01.1080p.WEB.x264-GRP", UnifiedCategory.Serie);
        var result = await rig.FetchAsync(id);
        Assert.True(result.Ok, JsonSerializer.Serialize(result.Body));
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Equal(600, row.TmdbId); Assert.Equal("tv", row.TmdbMediaType);
        Assert.Equal("600", row.ExtProviderId); Assert.Equal("Mock", row.ExtOverview);
        Assert.Contains("tmdb-tv-600-v2-", row.PosterFile!);
        Assert.All(rig.TmdbSearchRequests, r => Assert.DoesNotContain("first_air_date_year", r.Query));
    }

    [Fact]
    public async Task FanartOnlyEnrichesTheAcceptedVerifiedSeries()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.SeriesTmdbFallbackFanart, completeTmdbMetadata: true);
        var id = rig.IngestRelease("Show.Fanart.S01.1080p.WEB.x264-GRP", UnifiedCategory.Serie);
        var result = await rig.FetchAsync(id);
        Assert.True(result.Ok, JsonSerializer.Serialize(result.Body));
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Equal(700, row.TmdbId); Assert.Equal("tv", row.TmdbMediaType);
        Assert.Equal("fanart", row.PosterProvider); Assert.Equal("700", row.ExtProviderId);
        Assert.Contains("fanart-tv-700-v2-", row.PosterFile!);
    }

    [Fact]
    public async Task UnrelatedEmissionCandidatesAbstainWithoutPublishing()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.EmissionAmbiguousTmdbFallback, completeTmdbMetadata: true);
        var id = rig.IngestRelease("TF1.2024.1080p.WEB.x264-GRP", UnifiedCategory.Emission);
        var result = await rig.FetchAsync(id);
        Assert.False(result.Ok); Assert.Equal(422, result.StatusCode);
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Null(row.TmdbId); Assert.Null(row.ExtProviderId); Assert.Null(row.PosterFile);
    }
}
