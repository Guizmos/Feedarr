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
                var parameters = QueryHelpers.ParseQuery(request.RequestUri.Query);
                if (parameters.GetValueOrDefault("primary_release_year") is var year && year.Count > 0 && year != "1999")
                    return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK) {
                        Content = new StringContent("{\"page\":1,\"total_pages\":0,\"total_results\":0,\"results\":[]}", Encoding.UTF8, "application/json") });
                var page = int.Parse(parameters["page"]!);
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
        Assert.Equal(12, calls); // Four pages per locale plus four empty neighboring-year first pages.
        if (competitor) {
            Assert.Contains("ambiguous", JsonSerializer.Serialize(result.Body));
            Assert.Null(rig.GetReleaseForPoster(id)!.TmdbId);
            Assert.Null(rig.GetReleaseForPoster(id)!.PosterFile);
        }
        else Assert.Equal(100, rig.GetReleaseForPoster(id)!.TmdbId);
    }
    [Fact]
    public async Task CollectionBeyondCandidateCapacityUsesFullPageBudgetBeforeAbstaining()
    {
        // Pure collector fixture: no database or real provider access.
        var calls = 0;
        var collection = await Feedarr.Api.Services.Posters.VideoSearchCollector.CollectAsync(
            new[] { "A Distinct Film" }, new[] { "fr-FR", "en-US" }, (_, _, page, _) => {
                calls++;
                var response = VideoSearchCollectorTests.Page(120, page);
                return Task.FromResult<Feedarr.Api.Services.Tmdb.TmdbClient.VideoMatchingPage?>(response with {
                    Results = response.Results.Select(r => r with { Year = 2020 }).ToArray()
                });
            }, default);
        Assert.True(collection.Complete);
        Assert.Equal(12, calls);
        var projected = Feedarr.Api.Services.Matching.TmdbShadowCandidateAdapter.Project(collection.Observed);
        var diagnostic = Feedarr.Api.Services.Posters.VideoResultBudgetDiagnostic.CheckProjection(
            projected.Length, 100, collection.Counters);
        Assert.Equal("not_evaluable_projected_candidate_budget", diagnostic!.Reason);
        Assert.Equal("not_evaluable_result_budget", Feedarr.Api.Services.Posters.VideoResultBudgetDiagnostic.AbstentionReason);
    }
}
