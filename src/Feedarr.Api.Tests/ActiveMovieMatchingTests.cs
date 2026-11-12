using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Resilience;
using Microsoft.AspNetCore.WebUtilities;

namespace Feedarr.Api.Tests;

public sealed partial class ActiveMovieMatchingTests
{
    private static void Enable(AppOptions o) { o.MatchingV2MoviesEnabled = true; o.MatchingV2MoviesMinReleaseId = 1; }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static object Row(int id, string title = "The Matrix", string? poster = "/matrix.jpg", string year = "1999-03-31") =>
        new { id, title, original_title = title, poster_path = poster, release_date = year, original_language = "en" };
    private static HttpResponseMessage Page(params object[] rows) => Json(new { page = 1, total_pages = rows.Length == 0 ? 0 : 1, total_results = rows.Length, results = rows });
    private static long Movie(PosterMatchingContractTestRig rig, string raw = "The.Matrix.1999.1080p.BluRay.x264-GRP") => rig.IngestRelease(raw, UnifiedCategory.Film);

    [Fact]
    public void EnablingRequiresExplicitRolloutWatermarkAndBoundedBudgets()
    {
        var options = new AppOptions(); Assert.True(options.ValidMatchingV2Rollout);
        options.MatchingV2MoviesEnabled = true; Assert.False(options.ValidMatchingV2Rollout);
        options.MatchingV2MoviesMinReleaseId = 1; Assert.True(options.ValidMatchingV2Rollout);
        options.MatchingV2MoviesSearchBudget = 13; Assert.False(options.ValidMatchingV2Rollout);
    }

    [Fact]
    public async Task OffRetainsHistoricalQueriesAndPublication()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        var id = Movie(rig);
        Assert.True((await rig.FetchAsync(id)).Ok);
        Assert.Equal(100, rig.GetReleaseForPoster(id)!.TmdbId);
        Assert.All(rig.TmdbSearchRequests, request => Assert.False(QueryHelpers.ParseQuery(request.Query).ContainsKey("page")));
    }

    [Fact]
    public async Task OnAcceptUsesExistingTypedPublicationAndOfficialMetadata()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true, configure: Enable);
        var id = Movie(rig);
        var result = await rig.FetchAsync(id);
        Assert.True(result.Ok, JsonSerializer.Serialize(result.Body));
        var release = rig.GetReleaseForPoster(id)!;
        Assert.Equal(100, release.TmdbId); Assert.Equal("movie", release.TmdbMediaType);
        Assert.Equal("100", release.ExtProviderId);
        Assert.Contains("movie-100", release.PosterFile!);
        Assert.All(rig.TmdbSearchRequests, request => { Assert.Contains("page=1", request.Query); Assert.DoesNotContain("year=", request.Query); });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RejectionAmbiguityAndMissingCoverageNeverDownloadOrFallback(bool complete, bool competing)
    {
        var downloads = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: complete, configure: Enable,
            tmdbResponse: (r, _) =>
            {
                if (r.RequestUri!.Host.Contains("image.tmdb")) downloads++;
                return Task.FromResult<HttpResponseMessage?>(competing && r.RequestUri.AbsolutePath.Contains("search/movie") ? Page(Row(100), Row(200)) : null);
            });
        var id = Movie(rig, complete && !competing ? "Wrong.Title.1999.1080p.BluRay" : "The.Matrix.1999.1080p.BluRay");
        Assert.False((await rig.FetchAsync(id)).Ok);
        Assert.Null(rig.GetReleaseForPoster(id)!.TmdbId); Assert.Equal(0, downloads); Assert.Equal(0, rig.TmdbDetailsCalls);
    }

    [Fact]
    public async Task HistoricalCacheCannotAssociateWrongIdentity()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true, configure: Enable);
        var id = Movie(rig);
        rig.SeedPosterMatch("movie", "The Matrix", 1999, 999, null, null, "tmdb-movie-999-w500.jpg", "tmdb", "tmdb", "movie:999");
        Assert.True((await rig.FetchAsync(id)).Ok);
        Assert.Equal(100, rig.GetReleaseForPoster(id)!.TmdbId);
        Assert.NotEmpty(rig.TmdbSearchRequests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManualTitleOrEstablishedIdentityIsPreservedWithoutSearch(bool manual)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true, configure: Enable);
        var id = Movie(rig);
        if (manual) rig.SetManualTitleForTest(id);
        else { using var db = rig.Database.Open(); db.Execute("UPDATE media_entities SET tmdb_id=999,tmdb_media_type='movie' WHERE id=(SELECT entity_id FROM releases WHERE id=@id)", new { id }); }
        await rig.FetchAsync(id);
        Assert.Empty(rig.TmdbSearchRequests);
        if (!manual) Assert.Equal(999, rig.GetReleaseForPoster(id)!.TmdbId);
    }

    [Fact]
    public async Task WatermarkPreventsOldBacklogFromUsingV2()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, configure: o => { Enable(o); o.MatchingV2MoviesMinReleaseId = 1_000_000; });
        Assert.True((await rig.FetchAsync(Movie(rig))).Ok);
        Assert.All(rig.TmdbSearchRequests, request => Assert.DoesNotContain("page=", request.Query));
    }

    [Fact]
    public async Task BudgetExhaustionAbstainsEvenAfterExactHit()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true,
            configure: o => { Enable(o); o.MatchingV2MoviesSearchBudget = 1; });
        var id = Movie(rig);
        Assert.False((await rig.FetchAsync(id)).Ok);
        Assert.Null(rig.GetReleaseForPoster(id)!.TmdbId);
        Assert.Single(rig.TmdbSearchRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderErrorOrTimeoutAbstains(bool timeout)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, configure: Enable,
            tmdbResponse: (_, _) => timeout ? throw new TaskCanceledException() : throw new HttpRequestException("private-canary"));
        var id = Movie(rig);
        var result = await rig.FetchAsync(id);
        Assert.Equal(503, result.StatusCode); Assert.Null(rig.GetReleaseForPoster(id)!.TmdbId);
        Assert.DoesNotContain("private-canary", JsonSerializer.Serialize(result.Body));
    }

    [Fact]
    public async Task PaginationFindsCloseCompetitorOnSecondPage()
    {
        var requests = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, configure: Enable,
            tmdbResponse: (r, _) =>
            {
                requests++;
                var page = int.Parse(QueryHelpers.ParseQuery(r.RequestUri!.Query)["page"]!);
                var rows = page == 1 ? Enumerable.Range(100, 20).Select(i => Row(i, i == 100 ? "The Matrix" : $"Different Work {i}")).ToArray() : new[] { Row(999) };
                return Task.FromResult<HttpResponseMessage?>(Json(new { page, total_pages = 2, total_results = 21, results = rows }));
            });
        var id = Movie(rig);
        Assert.False((await rig.FetchAsync(id)).Ok);
        Assert.Null(rig.GetReleaseForPoster(id)!.TmdbId);
        Assert.InRange(requests, 2, 4);
    }

    [Fact]
    public async Task MissingSelectedPosterNeverChoosesRunnerUp()
    {
        var requests = new List<string>();
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, configure: Enable,
            tmdbResponse: (r, _) =>
            {
                requests.Add(r.RequestUri!.AbsoluteUri);
                return Task.FromResult<HttpResponseMessage?>(r.RequestUri.AbsolutePath.Contains("search/movie")
                    ? Page(Row(100, poster: null), Row(200, "Different Work"))
                    : r.RequestUri.AbsolutePath.Contains("/images") ? Json(new { id = 100, posters = Array.Empty<object>() }) : null);
            });
        var id = Movie(rig);
        Assert.False((await rig.FetchAsync(id)).Ok);
        Assert.Null(rig.GetReleaseForPoster(id)!.TmdbId);
        Assert.DoesNotContain(requests, r => r.Contains("/movie/200") || r.Contains("/movies/200"));
        Assert.DoesNotContain(requests, r => r.Contains("image.tmdb.org"));
    }

    [Fact]
    public async Task AllGeneratedVariantsAreCoveredWithoutUsingHypothesisAsYearFilter()
    {
        var queries = new List<string>();
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, configure: Enable,
            tmdbResponse: (r, _) =>
            {
                var parameters = QueryHelpers.ParseQuery(r.RequestUri!.Query);
                queries.Add(parameters["query"]!);
                Assert.False(parameters.ContainsKey("year"));
                return Task.FromResult<HttpResponseMessage?>(Page());
            });
        Assert.False((await rig.FetchAsync(Movie(rig, "Blade.Runner.2049.1080p"))).Ok);
        Assert.Contains("Blade Runner 2049", queries); Assert.Contains("Blade Runner", queries);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("count")]
    [InlineData("page")]
    [InlineData("overflow")]
    public async Task InvalidOrUncoveredProviderPagesAbstain(string defect)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, configure: Enable,
            tmdbResponse: (_, _) => Task.FromResult<HttpResponseMessage?>(Json(new {
                page = defect == "page" ? 2 : 1,
                total_pages = defect == "overflow" ? 4 : 1,
                total_results = defect == "overflow" ? 80 : defect == "count" ? 2 : 1,
                results = new[] { new { id = 100, title = "The Matrix", release_date = "1999-03-31", media_type = defect == "type" ? "tv" : "movie" } }
            })));
        var id = Movie(rig);
        Assert.False((await rig.FetchAsync(id)).Ok); Assert.Null(rig.GetReleaseForPoster(id)!.TmdbId);
    }

    [Fact]
    public async Task TwoReleasesSharingAnEntityCannotReplaceFirstAcceptedIdentity()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true, configure: Enable);
        var first = Movie(rig);
        var second = Movie(rig);
        Assert.True((await rig.FetchAsync(first)).Ok);
        var searches = rig.TmdbSearchRequests.Count;
        await rig.FetchAsync(second);
        Assert.Equal(100, rig.GetReleaseForPoster(second)!.TmdbId);
        Assert.Equal(searches, rig.TmdbSearchRequests.Count);
    }

    private sealed class CountingFailureHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    [Fact]
    public async Task ExplicitSearchRequestDisablesTransportRetries()
    {
        var transport = new CountingFailureHandler();
        using var http = new HttpClient(new TransientHttpRetryHandler { InnerHandler = transport });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/search/movie");
        request.Options.Set(TransientHttpRetryHandler.DisableRetries, true);
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task ConcurrentCorrectionDiscardsAcceptedResultBeforeDownload()
    {
        PosterMatchingContractTestRig? rig = null;
        var downloads = 0;
        using (rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true, configure: Enable,
            tmdbResponse: (r, _) =>
            {
                if (r.RequestUri!.Host.Contains("image.tmdb")) downloads++;
                if (r.RequestUri.AbsolutePath.Contains("search/movie"))
                {
                    using var db = rig!.Database.Open();
                    db.Execute("UPDATE media_entities SET tmdb_id=999,tmdb_media_type='movie',match_revision='concurrent' WHERE id=(SELECT entity_id FROM releases LIMIT 1)");
                }
                return Task.FromResult<HttpResponseMessage?>(null);
            }))
        {
            var id = Movie(rig);
            Assert.False((await rig.FetchAsync(id)).Ok);
            Assert.Equal(999, rig.GetReleaseForPoster(id)!.TmdbId); Assert.Equal(0, downloads);
        }
    }
}
