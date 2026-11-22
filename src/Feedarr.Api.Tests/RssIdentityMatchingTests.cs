using System.Net;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Tests;

public sealed class RssIdentityMatchingTests
{
    private static void Rss(PosterMatchingContractTestRig rig, long id, int? tmdb = null, string? imdb = null, int? tvdb = null)
    {
        using var db = rig.Database.Open();
        db.Execute("UPDATE releases SET raw_json=@json WHERE id=@id", new { id, json = JsonSerializer.Serialize(new RssMatchingEvidence(1, tmdb, tvdb, imdb, null)) });
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) {
        Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task TmdbHasPriorityWithoutTitleYearSynopsisOrSearchAndReusesVerifiedAssets()
    {
        var calls = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true,
            tmdbResponse: (request, _) => {
                calls++;
                Assert.DoesNotContain("/find/", request.RequestUri!.AbsolutePath);
                return Task.FromResult<HttpResponseMessage?>(null);
            });
        var first = rig.IngestRelease("Unrelated.Alpha.2020.1080p.WEB.H264-TEAM", UnifiedCategory.Film);
        Rss(rig, first, tmdb: 100, imdb: "tt1234567");
        Assert.True((await rig.FetchAsync(first)).Ok);
        var used = calls;
        var second = rig.IngestRelease("Another.Beta.1980.1080p.WEB.H264-TEAM", UnifiedCategory.Film);
        Rss(rig, second, tmdb: 100);
        Assert.True((await rig.FetchAsync(second)).Ok);
        Assert.Equal(used, calls);
        Assert.Empty(rig.TmdbSearchRequests);
        Assert.Equal(100, rig.GetReleaseForPoster(second)!.TmdbId);
        Assert.Equal(rig.GetReleaseForPoster(first)!.PosterFile, rig.GetReleaseForPoster(second)!.PosterFile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalResolutionUsesOnlyTheExpectedMediaType(bool tv)
    {
        var finds = 0;
        using var rig = new PosterMatchingContractTestRig(tv ? PosterContractScenario.SeriesTvmazeMissTmdb : PosterContractScenario.MovieTmdbHit,
            completeTmdbMetadata: true, tmdbResponse: (request, _) => {
                if (request.RequestUri!.AbsolutePath.EndsWith("/movie/999"))
                    return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.NotFound));
                if (!request.RequestUri!.AbsolutePath.Contains("/find/")) return Task.FromResult<HttpResponseMessage?>(null);
                finds++;
                Assert.Contains(tv ? "external_source=tvdb_id" : "external_source=imdb_id", request.RequestUri.Query);
                return Task.FromResult<HttpResponseMessage?>(Json(new { movie_results = new[] { new { id = 100 } }, tv_results = new[] { new { id = 600 } } }));
            });
        var id = rig.IngestRelease("Unrelated.Title.1980.S01.1080p.WEB.H264-TEAM", tv ? UnifiedCategory.Serie : UnifiedCategory.Film);
        Rss(rig, id, tmdb: tv ? null : 999, imdb: tv ? null : "tt1234567", tvdb: tv ? 12345 : null);
        var result = await rig.FetchAsync(id);
        Assert.True(result.Ok, JsonSerializer.Serialize(result.Body));
        Assert.Equal(1, finds);
        Assert.Empty(rig.TmdbSearchRequests);
        Assert.Equal(tv ? 600 : 100, rig.GetReleaseForPoster(id)!.TmdbId);
        Assert.Equal(tv ? "tv" : "movie", rig.GetReleaseForPoster(id)!.TmdbMediaType);
    }

    [Theory]
    [InlineData(0, "tt0000000")]
    [InlineData(999, null)]
    public async Task DummyOrUnresolvableIdentityFallsBackToTheExistingTextualPath(int tmdb, string? imdb)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true,
            tmdbResponse: (request, _) => {
                Assert.DoesNotContain("/find/tt0000000", request.RequestUri!.AbsolutePath);
                return Task.FromResult<HttpResponseMessage?>(request.RequestUri.AbsolutePath.EndsWith("/movie/999")
                    ? new(HttpStatusCode.NotFound) : null);
            });
        var id = rig.IngestRelease("The.Matrix.1999.1080p.WEB.H264-TEAM", UnifiedCategory.Film);
        Rss(rig, id, tmdb, imdb);
        Assert.True((await rig.FetchAsync(id)).Ok);
        Assert.NotEmpty(rig.TmdbSearchRequests);
        Assert.Equal(100, rig.GetReleaseForPoster(id)!.TmdbId);
    }

    [Fact]
    public async Task ContradictoryRssCannotReplaceAnEstablishedIdentity()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true);
        var id = rig.IngestRelease("The.Matrix.1999.1080p.WEB.H264-TEAM", UnifiedCategory.Film);
        Assert.True((await rig.FetchAsync(id)).Ok);
        var before = rig.GetReleaseForPoster(id)!;
        Rss(rig, id, tmdb: 999);
        Assert.True((await rig.FetchAsync(id)).Ok);
        Assert.Equal(100, rig.GetReleaseForPoster(id)!.TmdbId);
        Assert.Equal(before.PosterFile, rig.GetReleaseForPoster(id)!.PosterFile);
    }

    [Fact]
    public async Task InvalidRssPosterRemainsARetryablePublicationFailureWithoutTextualFallback()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true,
            tmdbResponse: (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.Host == "image.tmdb.org"
                ? new(HttpStatusCode.OK) { Content = new StringContent("not-an-image") } : null));
        var id = rig.IngestRelease("Unrelated.Title.1980.1080p.WEB.H264-TEAM", UnifiedCategory.Film);
        Rss(rig, id, tmdb: 100);
        var result = await rig.FetchAsync(id);
        Assert.False(result.Ok);
        Assert.Equal(503, result.StatusCode);
        Assert.Empty(rig.TmdbSearchRequests);
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Contains("v2:publication:publication_error", row.PosterLastError);
        Assert.Null(row.TmdbId); Assert.Null(row.ExtProviderId); Assert.Null(row.PosterFile);
        Assert.Empty(Directory.EnumerateFiles(rig.PostersDirectory, "*.jpg", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongTypeFallsBackButFailureAfterResolutionNeverSelectsAnotherWork(bool creditsFailure)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true,
            tmdbResponse: (request, _) => {
                var path = request.RequestUri!.AbsolutePath;
                if (!creditsFailure && path.EndsWith("/movie/999"))
                    return Task.FromResult<HttpResponseMessage?>(Json(new { id = 999, media_type = "tv", title = "Wrong family" }));
                if (creditsFailure && path.EndsWith("/movie/100/credits"))
                    return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.ServiceUnavailable));
                return Task.FromResult<HttpResponseMessage?>(null);
            });
        var id = rig.IngestRelease("The.Matrix.1999.1080p.WEB.H264-TEAM", UnifiedCategory.Film);
        Rss(rig, id, tmdb: creditsFailure ? 100 : 999);
        var result = await rig.FetchAsync(id);
        if (creditsFailure)
        {
            Assert.False(result.Ok);
            Assert.Empty(rig.TmdbSearchRequests);
            Assert.Null(rig.GetReleaseForPoster(id)!.TmdbId);
        }
        else
        {
            Assert.True(result.Ok);
            Assert.NotEmpty(rig.TmdbSearchRequests);
            Assert.Equal(100, rig.GetReleaseForPoster(id)!.TmdbId);
        }
    }
}
