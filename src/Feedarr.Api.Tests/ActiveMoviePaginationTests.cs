using System.Net;
using System.Text;
using System.Text.Json;
using Feedarr.Api.Models;
using Microsoft.AspNetCore.WebUtilities;

namespace Feedarr.Api.Tests;

public sealed class ActiveMoviePaginationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteFourPageCollectionIncludesLateCandidateAndCompetitor(bool competitor)
    {
        var calls = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit,
            completeTmdbMetadata: true, tmdbResponse: (request, _) => {
                if (!request.RequestUri!.AbsolutePath.Contains("search/movie")) return Task.FromResult<HttpResponseMessage?>(null);
                calls++;
                var page = int.Parse(QueryHelpers.ParseQuery(request.RequestUri.Query)["page"]!);
                var rows = Enumerable.Range((page - 1) * 20 + 1, 20).Select(id => new {
                    id = id == 80 ? 100 : id, title = id == 80 || competitor && id == 79 ? "The Matrix" : $"Unrelated Work {id}",
                    original_title = id == 80 || competitor && id == 79 ? "The Matrix" : $"Unrelated Work {id}",
                    release_date = "1999-03-31", poster_path = "/matrix.jpg", original_language = "en"
                }).ToArray();
                return Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent(JsonSerializer.Serialize(new { page, total_pages = 4, total_results = 80, results = rows }), Encoding.UTF8, "application/json") });
            });
        var id = rig.IngestRelease("The.Matrix.1999.1080p.BluRay.x264-GRP", UnifiedCategory.Film);
        var result = await rig.FetchAsync(id);
        Assert.Equal(!competitor, result.Ok);
        Assert.Equal(8, calls);
        if (competitor) {
            Assert.Contains("ambiguous", JsonSerializer.Serialize(result.Body));
            Assert.Null(rig.GetReleaseForPoster(id)!.TmdbId);
            Assert.Null(rig.GetReleaseForPoster(id)!.PosterFile);
        }
        else Assert.Equal(100, rig.GetReleaseForPoster(id)!.TmdbId);
    }
    [Fact]
    public async Task CollectionBeyondExistingCandidateCapacityStillAbstainsImmediately()
    {
        var calls = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit,
            completeTmdbMetadata: true, tmdbResponse: (request, _) => {
                if (!request.RequestUri!.AbsolutePath.Contains("search/movie")) return Task.FromResult<HttpResponseMessage?>(null);
                calls++;
                return Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent(JsonSerializer.Serialize(new { page = 1, total_pages = 6, total_results = 120,
                        results = Enumerable.Range(1, 20).Select(id => new { id, title = "The Matrix", original_title = "The Matrix", release_date = "1999-03-31" }) }), Encoding.UTF8, "application/json") });
            });
        var id = rig.IngestRelease("The.Matrix.1999.1080p.BluRay.x264-GRP", UnifiedCategory.Film);
        var result = await rig.FetchAsync(id);
        Assert.False(result.Ok);
        Assert.Contains("not_evaluable_result_budget", JsonSerializer.Serialize(result.Body));
        Assert.Equal(1, calls);
        Assert.Null(rig.GetReleaseForPoster(id)!.TmdbId);
        Assert.Null(rig.GetReleaseForPoster(id)!.PosterFile);
    }
}
