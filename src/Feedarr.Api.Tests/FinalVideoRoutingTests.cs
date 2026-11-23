using System.Text.Json;
using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Posters;

namespace Feedarr.Api.Tests;

public sealed class FinalVideoRoutingTests
{
    [Theory]
    [InlineData(UnifiedCategory.Animation, "2040", "The.Matrix.1999.1080p.BluRay", "movie")]
    [InlineData(UnifiedCategory.Spectacle, "2040", "The.Matrix.1999.1080p.BluRay", "movie")]
    [InlineData(UnifiedCategory.Autre, "2040", "The.Matrix.1999.1080p.BluRay", "movie")]
    [InlineData(UnifiedCategory.Animation, "5040", "Show.S01E02.1080p.WEB", "tv")]
    [InlineData(UnifiedCategory.Spectacle, null, "Show.S01E02.1080p.WEB", "tv")]
    [InlineData(UnifiedCategory.Autre, "5040", "Show.1080p.WEB", "tv")]
    [InlineData(UnifiedCategory.Autre, null, "Show.2026.11.14.1080p.WEB", "tv")]
    [InlineData(UnifiedCategory.Animation, "2040", "Show.S01E02.1080p.WEB", "tv")]
    [InlineData(UnifiedCategory.Animation, "2020,102060", "Le.Gros.Laboratoire.S07E09.VFQ.1080p.WEB", "tv")]
    [InlineData(UnifiedCategory.Animation, "2040", "Show.S01.1080p.WEB", null)]
    [InlineData(UnifiedCategory.Spectacle, "2040", "Show.S01E02.1080p.WEB", null)]
    [InlineData(UnifiedCategory.Spectacle, "2040,5040", "Show.1080p.WEB", null)]
    [InlineData(UnifiedCategory.Autre, "3040", "Album.S01E02.1080p", null)]
    [InlineData(UnifiedCategory.Autre, null, "Unknown.2020.1080p.BluRay", null)]
    public void RoutingUsesIndependentEvidenceAndRejectsContradictions(UnifiedCategory category, string? ids, string raw, string? expected)
    {
        var release = new ReleaseForPoster { Title = raw, CategoryIds = ids, MediaType = "movie" };
        Assert.Equal(expected, PosterFetchService.ResolveVideoType(release, category));
    }

    [Theory]
    [InlineData(UnifiedCategory.Animation)]
    [InlineData(UnifiedCategory.Spectacle)]
    [InlineData(UnifiedCategory.Autre)]
    public async Task ConfirmedTvUsesOnlyTvV2AndKeepsTheStoredCategory(UnifiedCategory category)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeHit, completeTmdbMetadata: true,
            tmdbResponse: (request, _) => {
                Assert.DoesNotContain("/movie/", request.RequestUri!.AbsolutePath);
                return Task.FromResult<System.Net.Http.HttpResponseMessage?>(null);
            });
        var id = rig.IngestRelease("Show.Tvmaze.Hit.S01E02.1080p.WEB.x264-GRP", category);
        using (var conn = rig.Database.Open())
            conn.Execute("UPDATE releases SET unified_category=@storedCategory,category_id=5040,std_category_id=5040,spec_category_id=NULL,category_ids='5040' WHERE id=@id", new { id, storedCategory = category.ToString() });
        var result = await rig.FetchAsync(id);
        Assert.True(result.Ok, JsonSerializer.Serialize(result.Body));
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Equal("tvmaze", row.ExtProvider); Assert.Equal("3001", row.ExtProviderId);
        Assert.Equal(category.ToString(), row.UnifiedCategory);
        Assert.Contains("-tv-3001-v2-", row.PosterFile!);
        var calls = rig.TmdbSearchRequests.Count;
        Assert.True((await rig.FetchAsync(id)).Ok);
        Assert.Equal(calls, rig.TmdbSearchRequests.Count);
        Assert.Equal(row.PosterFile, rig.GetReleaseForPoster(id)!.PosterFile);
    }

    [Theory]
    [InlineData("Autre", null)]
    [InlineData("not-a-real-category", null)]
    [InlineData("Animation", "2040,5040")]
    public async Task UnknownOrConflictingTypeCannotUseHistoricalCacheOrWrite(string category, string? ids)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, tmdbResponse: (_, _) => throw new InvalidOperationException("No provider call allowed"));
        var id = rig.IngestRelease("The.Matrix.1999.1080p.BluRay.x264-GRP", UnifiedCategory.Autre);
        using (var conn = rig.Database.Open())
            conn.Execute("UPDATE releases SET unified_category=@category,category_id=NULL,std_category_id=NULL,spec_category_id=NULL,category_ids=@ids WHERE id=@id", new { id, category, ids });
        rig.SeedPosterMatch("movie", "The Matrix", 1999, 999, null, null, "historical.jpg", posterProvider: "tmdb", posterProviderId: "movie:999");
        var result = await rig.FetchAsync(id);
        Assert.False(result.Ok); Assert.Equal(422, result.StatusCode);
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Null(row.ExtProviderId); Assert.Null(row.TmdbId); Assert.Null(row.PosterFile);
        Assert.Empty(rig.TmdbSearchRequests);
    }

    [Fact]
    public async Task UnknownCategoryWithMovieEvidenceUsesMovieV2()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true);
        var id = rig.IngestRelease("The.Matrix.1999.1080p.BluRay.x264-GRP", UnifiedCategory.Autre);
        using (var conn = rig.Database.Open()) conn.Execute("UPDATE releases SET unified_category='not-a-real-category' WHERE id=@id", new { id });
        Assert.True((await rig.FetchAsync(id)).Ok);
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Equal(100, row.TmdbId); Assert.Equal("movie", row.TmdbMediaType);
        Assert.Equal("not-a-real-category", row.UnifiedCategory);
        Assert.Contains("-movie-100-v2-", row.PosterFile!);
    }

    [Fact]
    public async Task UnknownCategoryPreservesEstablishedAndManualIdentitiesWithoutReclassification()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        var id = rig.CreateRelease("Unknown", "Unknown", null, UnifiedCategory.Autre, "unknown", tmdbId: 900);
        Assert.True((await rig.FetchAsync(id)).Ok);
        Assert.Equal(900, rig.GetReleaseForPoster(id)!.TmdbId);
        var manual = rig.CreateRelease("Manual", "Manual", null, UnifiedCategory.Autre, "unknown");
        rig.Repository.SetManualMatch(manual, "comicvine", "comic", "9900");
        Assert.True((await rig.FetchAsync(manual)).Ok);
        Assert.Equal("9900", rig.GetReleaseForPoster(manual)!.ManualMatchId);
        Assert.Empty(rig.TmdbSearchRequests);
    }
    [Fact]
    public void EpisodicAnimationNeverOverridesAnEstablishedMovieNamespace()
    {
        Assert.Null(PosterFetchService.ResolveVideoType(new ReleaseForPoster {
            Title = "Show.S01E02.1080p.WEB", CategoryIds = "2020", TmdbMediaType = "movie", TmdbId = 85113
        }, UnifiedCategory.Animation));
    }

}
