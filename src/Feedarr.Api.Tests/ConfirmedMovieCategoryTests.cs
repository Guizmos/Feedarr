using System.Net;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Posters;

namespace Feedarr.Api.Tests;

public sealed class ConfirmedMovieCategoryTests
{
    [Theory]
    [InlineData(UnifiedCategory.Animation)]
    [InlineData(UnifiedCategory.Spectacle)]
    public void RoutingNeedsMovieEvidenceAndRejectsEpisodicContradictions(UnifiedCategory category)
    {
        var release = new ReleaseForPoster { Title = "The.Matrix.1999.1080p.BluRay.x264-GRP", MediaType = "movie" };
        Assert.False(PosterFetchService.UseVideoV2(release, category)); // Derived movie default is insufficient.
        release.CategoryIds = "2040,100007";
        Assert.True(PosterFetchService.UseVideoV2(release, category));
        release.CategoryIds = "2040,5040";
        Assert.False(PosterFetchService.UseVideoV2(release, category));
        release.CategoryIds = "2040";
        release.Title = "A.Show.S01E02.2024.1080p.WEB.x264-GRP";
        // Explicit episodes route Animation to TV; Spectacle keeps the movie conflict guard.
        Assert.Equal(category == UnifiedCategory.Animation ? "tv" : null,
            PosterFetchService.ResolveVideoType(release, category));
        release.Title = "A.Show.2024.02.01.1080p.WEB.x264-GRP";
        Assert.False(PosterFetchService.UseVideoV2(release, category));
        release.Title = "The.Matrix.1999.1080p.BluRay.x264-GRP";
        release.MediaType = "series";
        Assert.False(PosterFetchService.UseVideoV2(release, category));
        Assert.True(PosterFetchService.UseVideoV2(release, UnifiedCategory.Serie));
    }

    [Theory]
    [InlineData(UnifiedCategory.Animation)]
    [InlineData(UnifiedCategory.Spectacle)]
    public async Task ConfirmedMoviePublishesAtomicallyAndPreservesItsIdentityOnReplay(UnifiedCategory category)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true);
        var id = rig.IngestRelease("The.Matrix.1999.1080p.BluRay.x264-GRP", category);
        var result = await rig.FetchAsync(id);
        Assert.True(result.Ok, JsonSerializer.Serialize(result.Body));
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Equal(100, row.TmdbId);
        Assert.Equal("movie", row.TmdbMediaType);
        Assert.Contains("-v2-", row.PosterFile!);
        Assert.False(string.IsNullOrWhiteSpace(row.ExtOverview));
        var calls = rig.TmdbSearchRequests.Count;
        Assert.True((await rig.FetchAsync(id)).Ok);
        Assert.Equal(calls, rig.TmdbSearchRequests.Count);
        Assert.Equal(row.PosterFile, rig.GetReleaseForPoster(id)!.PosterFile);
    }

    [Theory]
    [InlineData(UnifiedCategory.Animation)]
    [InlineData(UnifiedCategory.Spectacle)]
    public async Task MissingTmdbImageUsesFanartForTheSameAcceptedIdentity(UnifiedCategory category)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbFallbackFanart,
            completeTmdbMetadata: true, tmdbResponse: (request, _) => Task.FromResult<HttpResponseMessage?>(
                request.RequestUri!.Host == "image.tmdb.org" ? new(HttpStatusCode.ServiceUnavailable) : null));
        var id = rig.IngestRelease("Fallback.Movie.2001.1080p.BluRay.x264-GRP", category);
        var result = await rig.FetchAsync(id);
        Assert.True(result.Ok, JsonSerializer.Serialize(result.Body));
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Equal(200, row.TmdbId);
        Assert.Equal("movie", row.TmdbMediaType);
        Assert.Equal("200", row.ExtProviderId);
        Assert.Equal("fanart", row.PosterProvider);
        Assert.Contains("fanart-movie-200-v2-", row.PosterFile!);
    }

    [Theory]
    [InlineData(UnifiedCategory.Animation)]
    [InlineData(UnifiedCategory.Spectacle)]
    public async Task UncertainTypeAbstainsWithoutProviderCallsOrPublication(UnifiedCategory category)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        var id = rig.IngestRelease("The.Matrix.1999.1080p.BluRay.x264-GRP", category);
        using (var db = rig.Database.Open())
            db.Execute("UPDATE releases SET category_id=NULL,std_category_id=NULL,spec_category_id=NULL,category_ids=NULL WHERE id=@id", new { id });
        var result = await rig.FetchAsync(id);
        Assert.False(result.Ok);
        Assert.Equal(422, result.StatusCode);
        Assert.Null(rig.GetReleaseForPoster(id)!.PosterFile);
        Assert.Null(rig.GetReleaseForPoster(id)!.ExtProviderId);
        Assert.Empty(rig.TmdbSearchRequests);
    }

    [Theory]
    [InlineData(UnifiedCategory.Animation)]
    [InlineData(UnifiedCategory.Spectacle)]
    public async Task EstablishedIdentityIsPreservedEvenWhenTypeEvidenceIsMissing(UnifiedCategory category)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        var id = rig.IngestRelease("The.Matrix.1999.1080p.BluRay.x264-GRP", category);
        using (var db = rig.Database.Open())
            db.Execute("UPDATE media_entities SET tmdb_id=999,tmdb_media_type='tv' WHERE id=(SELECT entity_id FROM releases WHERE id=@id)", new { id });
        Assert.True((await rig.FetchAsync(id)).Ok);
        Assert.Empty(rig.TmdbSearchRequests);
        Assert.Equal(999, rig.GetReleaseForPoster(id)!.TmdbId);
        Assert.Equal("tv", rig.GetReleaseForPoster(id)!.TmdbMediaType);
    }
}
