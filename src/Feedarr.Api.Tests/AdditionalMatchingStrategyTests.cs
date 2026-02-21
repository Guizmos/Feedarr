using Feedarr.Api.Models;

namespace Feedarr.Api.Tests;

public sealed class AdditionalMatchingStrategyTests
{
    [Fact]
    public async Task Anime_JikanHit_ReturnsPoster()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.AnimeJikanHit);
        var releaseId = rig.CreateRelease("Naruto", "Naruto", 2002, UnifiedCategory.Anime, "anime");

        var actual = await rig.FetchSnapshotAsync(releaseId);

        Assert.True(actual.Ok);
        Assert.Equal("jikan", actual.PosterProvider);
        Assert.False(string.IsNullOrWhiteSpace(actual.PosterFile));
    }

    [Fact]
    public async Task Audio_AudioDbHit_ReturnsPoster()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.AudioAudioDbHit);
        var releaseId = rig.CreateRelease("Daft Punk - Around the World", "Daft Punk - Around the World", 1997, UnifiedCategory.Audio, "audio");

        var actual = await rig.FetchSnapshotAsync(releaseId);

        Assert.True(actual.Ok);
        Assert.Equal("theaudiodb", actual.PosterProvider);
        Assert.False(string.IsNullOrWhiteSpace(actual.PosterFile));
    }

    [Fact]
    public async Task Book_GoogleBooksHit_ReturnsPoster()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.BookGoogleBooksHit);
        var releaseId = rig.CreateRelease("Clean Architecture 9780134494166", "Clean Architecture 9780134494166", 2017, UnifiedCategory.Book, "book");

        var actual = await rig.FetchSnapshotAsync(releaseId);

        Assert.True(actual.Ok);
        Assert.Equal("googlebooks", actual.PosterProvider);
        Assert.False(string.IsNullOrWhiteSpace(actual.PosterFile));
    }

    [Fact]
    public async Task Comic_ComicVineHit_ReturnsPoster()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.ComicComicVineHit);
        var releaseId = rig.CreateRelease("Batman #1", "Batman #1", 2020, UnifiedCategory.Comic, "comic");

        var actual = await rig.FetchSnapshotAsync(releaseId);

        Assert.True(actual.Ok);
        Assert.Equal("comicvine", actual.PosterProvider);
        Assert.False(string.IsNullOrWhiteSpace(actual.PosterFile));
    }
}
