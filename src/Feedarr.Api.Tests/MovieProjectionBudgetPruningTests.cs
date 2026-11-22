using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Tests;

public sealed class MovieProjectionBudgetPruningTests
{
    private static CandidateRankingContext Context() => new(
        new[] { new SearchCandidate("A Distinct Film", 2020, CandidateYearEvidence.Parsed, SearchCandidateOrigin.Primary) },
        UnifiedCategory.Film, SearchMediaType.Movie);
    private static ProviderSearchCandidate Film(int id, int? year = 2020) =>
        new(new("tmdb", SearchMediaType.Movie, id.ToString()), "A Distinct Film", year);
    private static ProviderSearchCandidate[] Padding(int count = 100) =>
        Enumerable.Range(1000, count).Select(i => Film(i, 1980)).ToArray();
    private static ProviderSearchCandidate[] Prune(CandidateRankingContext context, ProviderSearchCandidate[] rows) =>
        SearchCandidateRanker.PruneMovieYearContradictionsBeforeBudget(context, rows);

    [Theory]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(101)]
    public void BoundaryPreservesTheHundredCandidateLimit(int count)
    {
        var rows = Enumerable.Range(1, count - 1).Select(i => Film(i)).Append(Film(1000, 1980)).ToArray();
        var retained = Prune(Context(), rows);

        Assert.Equal(100, new CandidateRankingPolicy().MaximumResults);
        Assert.Equal(count > 100 ? count - 1 : count, retained.Length);
        if (count <= 100) Assert.Same(rows, retained);
        AssertDecisionMatchesKnownCompetitors(Context(), rows, retained, rows[..^1]);
    }

    [Fact]
    public void DefinitiveYearRejectionsCanNoLongerBlockOneSupportedMovie()
    {
        var context = Context();
        var winner = Film(1);
        var rows = Padding().Prepend(winner).ToArray();
        var retained = Prune(context, rows);

        Assert.Equal(winner, Assert.Single(retained));
        Assert.Equal(CandidateDecision.Accept, SearchCandidateRanker.Rank(context, retained).Decision);
        AssertDecisionMatchesKnownCompetitors(context, rows, retained, new[] { winner });
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void MissingOrInvalidYearsAreRetained(int? year)
    {
        var uncertain = Film(1, year);
        var retained = Prune(Context(), Padding().Prepend(uncertain).ToArray());

        Assert.Equal(uncertain, Assert.Single(retained));
    }

    [Fact]
    public void ConflictingRowsOfOneIdentityRemainTogetherAndAmbiguous()
    {
        var context = Context();
        var conflict = new[] { Film(1), Film(1, 1980) };
        var rows = Padding(99).Concat(conflict).ToArray();
        var retained = Prune(context, rows);

        Assert.Equal(conflict, retained);
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(context, retained).Decision);
        AssertDecisionMatchesKnownCompetitors(context, rows, retained, conflict);
    }

    [Fact]
    public void MissingYearCompetitorStillPreventsAcceptanceOfANearbyYear()
    {
        var context = Context();
        var competitors = new[] { Film(1, 2021), Film(2, null) };
        var rows = Padding().Concat(competitors).ToArray();
        var retained = Prune(context, rows);

        Assert.Equal(competitors, retained);
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(context, retained).Decision);
        AssertDecisionMatchesKnownCompetitors(context, rows, retained, competitors);
    }

    [Theory]
    [InlineData(CandidateYearEvidence.None)]
    [InlineData(CandidateYearEvidence.Hypothesis)]
    public void UncertainQueriesDisablePruning(object evidenceValue)
    {
        var evidence = (CandidateYearEvidence)evidenceValue;
        var context = Context() with { Queries = new[] {
            new SearchCandidate("A Distinct Film", evidence == CandidateYearEvidence.None ? null : 2020, evidence, SearchCandidateOrigin.Primary) } };
        var rows = Padding().Append(Film(1)).ToArray();

        Assert.Same(rows, Prune(context, rows));
    }

    [Fact]
    public void CandidateCompatibleWithAnyQueryMustRemain()
    {
        var context = Context();
        context = context with { Queries = context.Queries.Append(
            new SearchCandidate("A Distinct Film", 1990, CandidateYearEvidence.Parsed, SearchCandidateOrigin.Primary)).ToArray() };
        var competitors = new[] { Film(1), Film(2, 1990) };
        var rows = Padding().Concat(competitors).ToArray();
        var retained = Prune(context, rows);

        Assert.Equal(competitors, retained);
        AssertDecisionMatchesKnownCompetitors(context, rows, retained, competitors);
    }

    [Theory]
    [InlineData(UnifiedCategory.Serie)]
    [InlineData(UnifiedCategory.Animation)]
    [InlineData(UnifiedCategory.Anime)]
    public void TvEnrichmentAndOtherCategoriesAreUntouched(UnifiedCategory category)
    {
        var context = Context() with { Category = category,
            ExpectedMediaType = category == UnifiedCategory.Animation ? SearchMediaType.Movie : SearchMediaType.Series };
        var rows = Padding().Append(Film(1)).ToArray();

        Assert.Same(rows, Prune(context, rows));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ProtectedContextsAreUntouched(int protection)
    {
        var context = Context() with { IsLocked = protection == 0, HasManualCorrection = protection == 1,
            EstablishedIdentity = protection == 2 ? Film(1).Identity : null };
        var rows = Padding().Append(Film(1)).ToArray();

        Assert.Same(rows, Prune(context, rows));
    }

    [Fact]
    public void ProjectionCombinesLocalesBeforePruningWholeIdentities()
    {
        var observed = new List<TmdbClient.ObservedSearchResult>();
        foreach (var language in new[] { "fr-FR", "en-US" })
        foreach (var id in Enumerable.Range(language == "fr-FR" ? 2 : 61, 59).Prepend(1))
            observed.Add(new(new(id, "A Distinct Film", "A Distinct Film", null, "movie", id == 1 ? 2020 : 1980, "en"), language));
        var original = TmdbShadowCandidateAdapter.Project(observed);
        var retained = TmdbShadowCandidateAdapter.Project(observed, Context());

        Assert.Equal(119, original.Length);
        Assert.Equal("1", Assert.Single(retained).Identity.Id);
        AssertDecisionMatchesKnownCompetitors(Context(), original, retained, original.Where(r => r.Identity.Id == "1").ToArray());
    }

    [Fact]
    public void TooManyViableCandidatesKeepTheSameBudgetDiagnosis()
    {
        var rows = Enumerable.Range(1, 101).Select(i => Film(i)).ToArray();
        var retained = Prune(Context(), rows);

        Assert.Same(rows, retained);
        var diagnostic = VideoResultBudgetDiagnostic.CheckProjection(retained.Length, 100, new(2, 6, 6, 101));
        Assert.Equal("not_evaluable_projected_candidate_budget", diagnostic!.Reason);
        Assert.Equal("not_evaluable_result_budget", VideoResultBudgetDiagnostic.AbstentionReason);
    }

    [Fact]
    public void AllRejectedInputKeepsTheExistingNonemptyBudgetDiagnosis()
    {
        var rows = Padding(101);
        Assert.Same(rows, Prune(Context(), rows));
    }

    private static void AssertDecisionMatchesKnownCompetitors(CandidateRankingContext context,
        ProviderSearchCandidate[] original, ProviderSearchCandidate[] retained, ProviderSearchCandidate[] competitors)
    {
        // No policy limit is increased for the reference: fixture competitors fit under 100.
        var expected = SearchCandidateRanker.Rank(context, competitors);
        var actual = SearchCandidateRanker.Rank(context, retained);
        Assert.Equal(expected.Decision, actual.Decision);
        Assert.Equal(expected.SelectedIdentity, actual.SelectedIdentity);
        Assert.Equal(expected.Reasons, actual.Reasons);
        var retainedIds = retained.Select(r => r.Identity).ToHashSet();
        foreach (var group in original.Where(r => !retainedIds.Contains(r.Identity)).GroupBy(r => r.Identity))
            Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(context, group.ToArray()).Decision);
    }
}
