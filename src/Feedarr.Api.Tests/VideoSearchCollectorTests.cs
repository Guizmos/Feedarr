using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Tests;

public sealed class VideoSearchCollectorTests
{
    private static readonly string[] Locales = { "fr-FR", "en-US" };
    private static CandidateRankingContext Context() => new(
        new[] { new SearchCandidate("A Distinct Film", 2020, CandidateYearEvidence.Parsed, SearchCandidateOrigin.Primary) },
        UnifiedCategory.Film, SearchMediaType.Movie);
    internal static TmdbClient.VideoMatchingPage Page(int total, int page, bool competitor = false)
    {
        var rows = Enumerable.Range((page - 1) * 20 + 1, Math.Min(20, total - (page - 1) * 20))
            .Select(id => new TmdbClient.SearchResult(id, "A Distinct Film", "A Distinct Film", null, "movie",
                id == total || competitor && id == total - 1 ? 2020 : 1980, "en")).ToArray();
        return new(rows, (int)Math.Ceiling(total / 20d), total);
    }

    [Theory]
    [InlineData(99, 10)]
    [InlineData(100, 10)]
    [InlineData(101, 12)]
    public async Task CompleteUnfilteredCollectionPrecedesProjectionAndRanking(int total, int expectedRequests)
    {
        var calls = new List<(string Language, int Page)>();
        var collection = await VideoSearchCollector.CollectAsync(new[] { "A Distinct Film" }, Locales,
            (_, language, page, _) => { calls.Add((language, page)); return Task.FromResult<TmdbClient.VideoMatchingPage?>(Page(total, page)); }, default);

        Assert.True(collection.Complete);
        Assert.Equal(expectedRequests, calls.Count);
        Assert.Equal(("fr-FR", 1), calls[0]);
        Assert.Equal(("en-US", 1), calls[1]);
        Assert.Equal(expectedRequests, collection.RequiredPages);
        Assert.Equal(total * 2, collection.Observed.Count);
        var projected = TmdbShadowCandidateAdapter.Project(collection.Observed, Context());
        Assert.True(projected.Length <= 100);
        Assert.Equal(CandidateDecision.Accept, SearchCandidateRanker.Rank(Context(), projected).Decision);
    }

    [Theory]
    [InlineData(120, 120, 12, true)]
    [InlineData(120, 140, 13, false)]
    public async Task CompletePlanMustFitExactlyWithinTwelvePages(int first, int second, int required, bool complete)
    {
        var calls = 0;
        var collection = await VideoSearchCollector.CollectAsync(new[] { "A Distinct Film" }, Locales,
            (_, language, page, _) => { calls++; return Task.FromResult<TmdbClient.VideoMatchingPage?>(Page(language == "fr-FR" ? first : second, page)); }, default);

        Assert.Equal(complete, collection.Complete);
        Assert.Equal(required, collection.RequiredPages);
        Assert.Equal(complete ? 12 : 2, calls);
        if (!complete)
        {
            Assert.Equal("not_evaluable_search_budget", collection.FailureReason);
            Assert.Empty(collection.Observed);
        }
    }

    [Fact]
    public async Task AThirteenthPageIsNeverRequested()
    {
        var calls = 0;
        var collection = await VideoSearchCollector.CollectAsync(new[] { "A Distinct Film" }, new[] { "en-US" },
            (_, _, page, _) => { calls++; return Task.FromResult<TmdbClient.VideoMatchingPage?>(Page(241, page)); }, default);

        Assert.False(collection.Complete);
        Assert.Equal(13, collection.RequiredPages);
        Assert.Equal(1, calls);
        Assert.Empty(collection.Observed);
    }

    [Fact]
    public async Task LastPageCompetitorPreventsPrematureAcceptance()
    {
        var collection = await VideoSearchCollector.CollectAsync(new[] { "A Distinct Film" }, Locales,
            (_, _, page, _) => Task.FromResult<TmdbClient.VideoMatchingPage?>(Page(101, page, competitor: true)), default);

        Assert.True(collection.Complete);
        Assert.Equal(12, collection.Counters.SearchRequests);
        var projected = TmdbShadowCandidateAdapter.Project(collection.Observed, Context());
        Assert.Equal(2, projected.Length);
        var ranked = SearchCandidateRanker.Rank(Context(), projected);
        Assert.Equal(CandidateDecision.Ambiguous, ranked.Decision);
        Assert.Null(ranked.SelectedIdentity);
    }

    [Theory]
    [InlineData("missing", "not_evaluable_provider_coverage")]
    [InlineData("changed-total", "not_evaluable_provider_changed_during_pagination")]
    [InlineData("changed-pages", "not_evaluable_provider_changed_during_pagination")]
    [InlineData("short-page", "not_evaluable_provider_coverage")]
    [InlineData("duplicate-page", "not_evaluable_duplicate_page_identity")]
    [InlineData("duplicate-row", "not_evaluable_duplicate_page_identity")]
    public async Task UncertainCoverageNeverExposesPartialCandidates(string fault, string reason)
    {
        var collection = await VideoSearchCollector.CollectAsync(new[] { "A Distinct Film" }, new[] { "en-US" },
            (_, _, page, _) => {
                var response = Page(40, page);
                if (page == 2) response = fault switch {
                    "missing" => null,
                    "changed-total" => response with { TotalResults = 41 },
                    "changed-pages" => response with { TotalPages = 3 },
                    "short-page" => response with { Results = response.Results.Take(19).ToArray() },
                    "duplicate-page" => Page(40, 1),
                    "duplicate-row" => response with { Results = response.Results.Take(19).Append(response.Results[0]).ToArray() },
                    _ => response
                };
                return Task.FromResult<TmdbClient.VideoMatchingPage?>(response);
            }, default);

        Assert.False(collection.Complete);
        Assert.Equal(reason, collection.FailureReason);
        Assert.Empty(collection.Observed);
        Assert.Equal(2, collection.Counters.SearchRequests);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(40, 1)]
    [InlineData(0, 2)]
    public async Task InvalidPaginationMetadataAbstains(int total, int pages)
    {
        var collection = await VideoSearchCollector.CollectAsync(new[] { "A Distinct Film" }, new[] { "en-US" },
            (_, _, _, _) => Task.FromResult<TmdbClient.VideoMatchingPage?>(new(Array.Empty<TmdbClient.SearchResult>(), pages, total)), default);
        Assert.False(collection.Complete);
        Assert.Equal("not_evaluable_provider_coverage", collection.FailureReason);
        Assert.Empty(collection.Observed);
    }

    [Fact]
    public async Task EmptyResultsStillCoverEveryLocale()
    {
        var collection = await VideoSearchCollector.CollectAsync(new[] { "A Distinct Film", "A Distinct Film" }, Locales,
            (_, _, _, _) => Task.FromResult<TmdbClient.VideoMatchingPage?>(new(Array.Empty<TmdbClient.SearchResult>(), 0, 0)), default);
        Assert.True(collection.Complete);
        Assert.Equal(2, collection.Counters.SearchRequests);
        Assert.Empty(collection.Observed);
    }

    [Fact]
    public async Task CancellationUsesTheSharedTokenAndCannotReturnCompleteCoverage()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VideoSearchCollector.CollectAsync(
            new[] { "A Distinct Film" }, Locales, (_, _, page, token) => {
                Assert.Equal(cancellation.Token, token);
                calls++;
                cancellation.Cancel();
                return Task.FromResult<TmdbClient.VideoMatchingPage?>(Page(101, page));
            }, cancellation.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CompleteCollectionStillAbstainsWhenMoreThanHundredRelevantCandidatesRemain()
    {
        var collection = await VideoSearchCollector.CollectAsync(new[] { "A Distinct Film" }, Locales,
            (_, _, page, _) => Task.FromResult<TmdbClient.VideoMatchingPage?>(Page(101, page) with {
                Results = Page(101, page).Results.Select(r => r with { Year = 2020 }).ToArray()
            }), default);
        Assert.True(collection.Complete);
        var projected = TmdbShadowCandidateAdapter.Project(collection.Observed, Context());
        Assert.Equal(101, projected.Length);
        var diagnostic = VideoResultBudgetDiagnostic.CheckProjection(projected.Length, 100, collection.Counters);
        Assert.Equal("not_evaluable_projected_candidate_budget", diagnostic!.Reason);
        Assert.Equal("not_evaluable_result_budget", VideoResultBudgetDiagnostic.AbstentionReason);
    }
}
