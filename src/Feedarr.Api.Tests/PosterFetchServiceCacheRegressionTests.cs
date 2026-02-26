using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Tests;

public sealed class PosterFetchServiceCacheRegressionTests
{
    [Fact]
    public async Task PosterFetch_DoesNotCacheMatch_WhenNoPosterSaved()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbNoPosterNoFanart);
        var releaseId = rig.CreateRelease("No Fanart Movie", "No Fanart Movie", 2002, UnifiedCategory.Film, "movie");

        var snapshot = await rig.FetchSnapshotAsync(releaseId);

        Assert.False(snapshot.Ok);
        Assert.Equal(404, snapshot.StatusCode);

        var normalizedTitle = TitleNormalizer.NormalizeTitle("No Fanart Movie");
        var cached = rig.TryGetCachedMatch("movie", normalizedTitle, 2002);
        Assert.Null(cached);
    }

    [Fact]
    public async Task PosterFetch_CachesMatch_WhenPosterSaved()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        var releaseId = rig.CreateRelease("The Matrix", "The Matrix", 1999, UnifiedCategory.Film, "movie");

        var snapshot = await rig.FetchSnapshotAsync(releaseId);

        Assert.True(snapshot.Ok);
        Assert.Equal(200, snapshot.StatusCode);

        var normalizedTitle = TitleNormalizer.NormalizeTitle("The Matrix");
        var cached = rig.TryGetCachedMatch("movie", normalizedTitle, 1999);
        Assert.NotNull(cached);
        Assert.False(string.IsNullOrWhiteSpace(cached!.PosterFile));
    }
}
