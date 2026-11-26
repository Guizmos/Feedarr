using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Tests;

public sealed class MovieYearSearchCollectorTests
{
    private static readonly string[] Locales = ["fr-FR", "en-US"];
    private static CandidateRankingContext Context() => new(
        SearchCandidateGenerator.Generate(new("F1.2025.MULTi.1080p.WEB.EAC3.5.1.H265-GL0P", UnifiedCategory.Film)),
        UnifiedCategory.Film, SearchMediaType.Movie);
    private static TmdbClient.SearchResult Row(int id = 1, string title = "F1", int year = 2025, string? original = null)
        => new(id, title, original ?? title, "/poster.jpg", "movie", year, "en");
    private static TmdbClient.VideoMatchingPage Page(params TmdbClient.SearchResult[] rows) => new(rows, rows.Length == 0 ? 0 : 1, rows.Length);
    private static Task<TmdbClient.VideoMatchingPage?> Response(TmdbClient.VideoMatchingPage page) => Task.FromResult<TmdbClient.VideoMatchingPage?>(page);
    private static CandidateRankingResult Rank(VideoSearchCollection collection, CandidateRankingContext? context = null)
    {
        Assert.True(collection.Complete);
        context ??= Context();
        return SearchCandidateRanker.Rank(context, TmdbShadowCandidateAdapter.Project(collection.Observed, context));
    }

    [Fact]
    public async Task ReliableFilmAcceptsAfterCompleteYearWindowInsteadOfSixteenGeneralPages()
    {
        var calls = new List<(string Title, string Language, int? Year)>();
        var diagnostics = new List<VideoCollectionDiagnostic>();
        var result = await MovieYearSearchCollector.CollectAsync(Context(), Locales, (title, language, page, year, _) => {
            calls.Add((title, language, year));
            // The general F1 search has 154 results, requiring 8 pages per locale.
            if (year is null) return Response(VideoSearchCollectorTests.Page(154, page));
            return Response(year == 2025 ? Page(Row()) : Page());
        }, default, diagnostics.Add);
        Assert.Equal("targeted_year", result.SearchMode);
        Assert.Equal(CandidateDecision.Accept, Rank(result).Decision);
        Assert.Equal(6, calls.Count);
        Assert.All(calls, c => Assert.Equal("F1", c.Title));
        Assert.Equal(new[] { 2024, 2025, 2026 }, calls.Select(c => c.Year!.Value).Distinct().Order());
        Assert.All(Locales, locale => Assert.Equal(3, calls.Count(c => c.Language == locale)));
        Assert.Single(TmdbShadowCandidateAdapter.Project(result.Observed, Context()));
        Assert.Equal(1, Rank(result).Candidates.Single().Confidence);
        Assert.Equal("complete", Assert.Single(diagnostics).Outcome);
    }

    [Theory]
    [InlineData(2024)]
    [InlineData(2025)]
    [InlineData(2026)]
    public async Task HomonymInExactOrAdjacentYearRemainsAmbiguous(int competitorYear)
    {
        var result = await MovieYearSearchCollector.CollectAsync(Context(), Locales, (_, _, _, year, _) => {
            Assert.NotNull(year); // Ambiguity is not hidden by falling back.
            var rows = new List<TmdbClient.SearchResult>();
            if (year == 2025) rows.Add(Row());
            if (year == competitorYear) rows.Add(Row(2, year: competitorYear));
            return Response(Page(rows.ToArray()));
        }, default);
        var ranked = Rank(result);
        Assert.Equal(CandidateDecision.Ambiguous, ranked.Decision);
        Assert.Contains("close-competing-identities", ranked.Reasons);
        Assert.Equal(2, ranked.Candidates.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OriginalOrLocalizedOfficialTitleSurvivesFrenchEnglishDeduplication(bool originalMatches)
    {
        var result = await MovieYearSearchCollector.CollectAsync(Context(), Locales, (_, language, _, year, _) =>
            Response(year == 2025 ? Page(Row(title: originalMatches ? "Film localisé" : "F1", original: originalMatches ? "F1" : "Original foreign title")) : Page()), default);
        var ranked = Rank(result);
        Assert.Equal(CandidateDecision.Accept, ranked.Decision);
        Assert.Single(ranked.Candidates);
        Assert.Contains(originalMatches ? "official-original-title-exact" : "official-localized-title-exact", ranked.Candidates[0].PositiveReasons);
    }

    [Fact]
    public async Task CompetitorOnFinalTargetedPagePreventsAcceptance()
    {
        var calls = 0;
        var result = await MovieYearSearchCollector.CollectAsync(Context(), Locales, (_, _, page, year, _) => {
            calls++;
            if (year != 2025) return Response(Page());
            var rows = page == 1 ? Enumerable.Range(1, 20).Select(i => Row(i, i == 1 ? "F1" : $"Unrelated Work {i}")).ToArray() : new[] { Row(999) };
            return Response(new(rows, 2, 21));
        }, default);
        Assert.Equal(8, calls);
        Assert.Equal(CandidateDecision.Ambiguous, Rank(result).Decision);
    }

    [Theory]
    [InlineData(false, UnifiedCategory.Film)]
    [InlineData(true, UnifiedCategory.Film)]
    [InlineData(false, UnifiedCategory.Animation)]
    [InlineData(true, UnifiedCategory.Animation)]
    public async Task IncompleteTargetedCoverageNeverExposesTheEarlyExactHit(bool budget, UnifiedCategory category)
    {
        var calls = 0;
        var diagnostics = new List<VideoCollectionDiagnostic>();
        var result = await MovieYearSearchCollector.CollectAsync(Context() with { Category = category }, Locales, (_, _, page, year, _) => {
            calls++;
            Assert.NotNull(year);
            if (!budget && page == 2) return Task.FromResult<TmdbClient.VideoMatchingPage?>(null);
            if (year != 2025) return Response(Page());
            var total = budget ? 141 : 21;
            var rows = Enumerable.Range((page - 1) * 20 + 1, Math.Min(20, total - (page - 1) * 20)).Select(i => Row(i)).ToArray();
            return Response(new(rows, (int)Math.Ceiling(total / 20d), total));
        }, default, diagnostics.Add);
        Assert.False(result.Complete);
        Assert.Empty(result.Observed);
        Assert.InRange(calls, 1, 12);
        Assert.Equal(budget ? "not_evaluable_search_budget" : "not_evaluable_provider_coverage", result.FailureReason);
        Assert.Equal("targeted_year", Assert.Single(diagnostics).SearchMode);
        Assert.Equal(result.FailureReason, diagnostics[0].Outcome);
    }

    [Fact]
    public async Task EmptyCompleteTargetedSearchFallsBackWithOneSharedBudget()
    {
        var calls = 0;
        var diagnostics = new List<VideoCollectionDiagnostic>();
        var result = await MovieYearSearchCollector.CollectAsync(Context(), Locales, (_, _, _, year, _) => {
            calls++;
            return Response(year.HasValue ? Page() : Page(Row()));
        }, default, diagnostics.Add);
        Assert.Equal("general_fallback", result.SearchMode);
        Assert.Equal(CandidateDecision.Accept, Rank(result).Decision);
        Assert.Equal(8, result.Counters.SearchRequests);
        Assert.Equal(calls, result.Counters.SearchRequests);
        Assert.Equal(8, result.RequiredPages);
        Assert.Equal(new[] { "targeted_year", "general_fallback" }, diagnostics.Select(d => d.SearchMode));
    }

    [Fact]
    public async Task FallbackCannotResetTheTwelveRequestBudgetOrExposePartialResults()
    {
        var calls = 0;
        var result = await MovieYearSearchCollector.CollectAsync(Context(), Locales, (_, _, page, year, _) => {
            calls++;
            return Response(year.HasValue ? Page() : VideoSearchCollectorTests.Page(61, page));
        }, default);
        Assert.False(result.Complete);
        Assert.Equal("general_fallback", result.SearchMode);
        Assert.Equal("not_evaluable_search_budget", result.FailureReason);
        Assert.Equal(14, result.RequiredPages); // Six targeted requests + eight required fallback pages.
        Assert.Equal(8, result.Counters.SearchRequests); // Abort as soon as the complete plan cannot fit.
        Assert.Equal(8, calls);
        Assert.Empty(result.Observed);
    }

    [Fact]
    public async Task AThirteenthRequestIsNeverSentWhenTargetedSearchConsumesTheWholeBudget()
    {
        var calls = 0;
        var result = await MovieYearSearchCollector.CollectAsync(Context(), Locales, (_, _, page, year, _) => {
            calls++;
            Assert.NotNull(year);
            return Response(new(Enumerable.Range((page - 1) * 20 + 1, page == 1 ? 20 : 1)
                .Select(i => Row(i, $"Unrelated {i}", year!.Value)).ToArray(), 2, 21));
        }, default);
        Assert.False(result.Complete);
        Assert.Equal(12, calls);
        Assert.Equal(12, result.Counters.SearchRequests);
        Assert.Empty(result.Observed);
    }

    [Fact]
    public async Task FallbackPreservesContradictoryProviderMetadataForTheSameIdentity()
    {
        var result = await MovieYearSearchCollector.CollectAsync(Context(), Locales, (_, _, _, year, _) =>
            Response(year.HasValue ? Page(Row(year: 2020)) : Page(Row())), default);
        Assert.Equal("general_fallback", result.SearchMode);
        var ranked = Rank(result);
        Assert.Equal(CandidateDecision.Ambiguous, ranked.Decision);
        Assert.Contains(ranked.Candidates.Single().Contradictions, c => c.Code == "conflicting-provider-records");
    }

    [Theory]
    [InlineData("missing-year")]
    [InlineData("hypothesis")]
    [InlineData("tv")]
    [InlineData("conflicting-query-years")]
    [InlineData("rss-identity")]
    public async Task UnreliableYearsAndOtherCategoriesKeepTheGeneralPlan(string scope)
    {
        var context = Context();
        context = scope switch {
            "missing-year" => context with { Queries = [new("F1", null, CandidateYearEvidence.None, SearchCandidateOrigin.Primary)] },
            "hypothesis" => context with { Queries = [new("F1", 2025, CandidateYearEvidence.Hypothesis, SearchCandidateOrigin.AmbiguousTrailingNumber)] },
            "tv" => context with { ExpectedMediaType = SearchMediaType.Series, Category = UnifiedCategory.Serie },
            "rss-identity" => context with { RssEvidence = new RssMatchingEvidence(1, 2, null, null, "Evidence") },
            _ => context with { Queries = [context.Queries[0], context.Queries[0] with { Year = 2024, Title = "Another" }] }
        };
        var calls = 0;
        var result = await MovieYearSearchCollector.CollectAsync(context, Locales, (_, _, _, year, _) => {
            calls++; Assert.Null(year); return Response(Page());
        }, default);
        Assert.True(result.Complete);
        Assert.Equal("general", result.SearchMode);
        Assert.Equal(context.Queries.Count * 2, calls);
    }

    [Theory]
    [InlineData(UnifiedCategory.Film)]
    [InlineData(UnifiedCategory.Animation)]
    [InlineData(UnifiedCategory.Spectacle)]
    [InlineData(UnifiedCategory.Autre)]
    public async Task ProvenMovieTypeUsesTheSameYearWindowRegardlessOfFunctionalCategory(UnifiedCategory category)
    {
        var context = Context() with { Category = category };
        var calls = 0;
        var result = await MovieYearSearchCollector.CollectAsync(context, Locales, (_, _, _, year, _) => {
            calls++; Assert.NotNull(year);
            return Response(year == 2025 ? Page(Row()) : Page());
        }, default);
        Assert.Equal("targeted_year", result.SearchMode);
        Assert.Equal(6, calls);
        Assert.Equal(CandidateDecision.Accept, Rank(result, context).Decision);
    }

    [Fact]
    public async Task EntirelyNumericTitlesRetainTheExistingIdentityProtection()
    {
        var context = Context() with { Queries = [new("1917", 2019, CandidateYearEvidence.Parsed, SearchCandidateOrigin.Primary)] };
        var result = await MovieYearSearchCollector.CollectAsync(context, Locales, (_, _, _, year, _) =>
            Response(year == 2019 ? Page(Row(title: "1917", year: 2019)) : Page()), default);
        var ranked = Rank(result, context);
        Assert.Equal(CandidateDecision.Ambiguous, ranked.Decision);
        Assert.Contains(ranked.Candidates.Single().Contradictions, c => c.Code == "numeric-title-needs-independent-identity");
    }
}
