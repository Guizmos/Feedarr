using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Tests;

public sealed class SearchCandidateRankerTests
{
    private static SearchIdentity Id(string id, SearchMediaType type = SearchMediaType.Movie, string provider = "tmdb")
        => new(provider, type, id);
    private static CandidateRankingContext Context(string title = "A Distinct Film", int? year = 2020,
        CandidateYearEvidence evidence = CandidateYearEvidence.Parsed)
        => new(new[] { new SearchCandidate(title, year, evidence, SearchCandidateOrigin.Primary) }, UnifiedCategory.Film, SearchMediaType.Movie);
    private static ProviderSearchCandidate Film(string id = "1", string title = "A Distinct Film", int? year = 2020)
        => new(Id(id), title, year);

    public static IEnumerable<object[]> RepresentativeCases()
    {
        yield return new object[] { "exact-title-year", Context(), new[] { Film() }, CandidateDecision.Accept, "exact-title" };
        yield return new object[] { "known-alternate", Context("Titre Français"), new[] { Film(title: "Original Title") with { AlternativeTitles = new[] { "Titre Français" } } }, CandidateDecision.Accept, "known-alternate-title-agrees" };
        yield return new object[] { "parsed-year-contradiction", Context(), new[] { Film(year: 1990) }, CandidateDecision.Reject, "parsed-year-mismatch" };
        yield return new object[] { "confirmed-year-contradiction", Context() with { ConfirmedYear = 2020 }, new[] { Film(year: 2021) }, CandidateDecision.Reject, "confirmed-year-mismatch" };
        yield return new object[] { "nearby-parsed-year", Context(), new[] { Film(year: 2021) }, CandidateDecision.Accept, "parsed-year-nearby" };
        yield return new object[] { "wrong-category", Context(), new[] { Film() with { Category = UnifiedCategory.Serie } }, CandidateDecision.Reject, "category-mismatch" };
        yield return new object[] { "wrong-media", Context(), new[] { Film() with { Identity = Id("1", SearchMediaType.Series) } }, CandidateDecision.Reject, "media-type-mismatch" };
        yield return new object[] { "close-identities", Context(), new[] { Film("1"), Film("2") }, CandidateDecision.Ambiguous, "close-competing-identities" };
        yield return new object[] { "numeric-title", Context("1917", 2019), new[] { Film(title: "1917", year: 2019) }, CandidateDecision.Ambiguous, "numeric-title-needs-independent-identity" };
        yield return new object[] { "number-collision", Context("Film 2"), new[] { Film(title: "Film 3") }, CandidateDecision.Reject, "title-number-mismatch" };
        yield return new object[] { "hypothesis-agrees", Context("Blade Runner", 2049, CandidateYearEvidence.Hypothesis), new[] { Film(title: "Blade Runner", year: 2049) }, CandidateDecision.Ambiguous, "hypothesis-needs-independent-evidence" };
        yield return new object[] { "hypothesis-disagrees", Context("Blade Runner", 2049, CandidateYearEvidence.Hypothesis), new[] { Film(title: "Blade Runner", year: 1982) }, CandidateDecision.Ambiguous, "hypothesis-year-disagrees" };
        yield return new object[] { "reversed-title", Context("Man Bites Dog"), new[] { Film(title: "Dog Bites Man") }, CandidateDecision.Reject, "title-similarity-insufficient" };
        yield return new object[] { "missing-year", Context(), new[] { Film(year: null) }, CandidateDecision.Ambiguous, "provider-year-unavailable" };
        yield return new object[] { "trusted-hint", Context(year: null, evidence: CandidateYearEvidence.None) with { TrustedIdentityHint = Id("1") }, new[] { Film() }, CandidateDecision.Accept, "trusted-identity-agrees" };
        yield return new object[] { "trusted-id-mismatch", Context() with { TrustedIdentityHint = Id("7") }, new[] { Film() }, CandidateDecision.Reject, "trusted-identity-mismatch" };
    }

    [Theory]
    [MemberData(nameof(RepresentativeCases))]
    public void Rank_RepresentativeDecisionsAreExplained(string name, object contextValue,
        object resultsValue, object decisionValue, string reason)
    {
        var context = (CandidateRankingContext)contextValue;
        var results = (ProviderSearchCandidate[])resultsValue;
        var decision = (CandidateDecision)decisionValue;
        var result = SearchCandidateRanker.Rank(context, results);
        Assert.True(decision == result.Decision, $"{name}: expected {decision}, received {result.Decision}");
        Assert.Contains(reason, result.Reasons.Concat(result.Candidates.SelectMany(c => c.PositiveReasons))
            .Concat(result.Candidates.SelectMany(c => c.Contradictions.Select(x => x.Code))));
        Assert.All(result.Candidates, c => Assert.InRange(c.Confidence, 0, 1));
        Assert.Equal(decision == CandidateDecision.Accept, result.SelectedIdentity is not null);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void PersistedLocksAndEstablishedIdentities_PreserveAssociation(bool locked, bool manual, bool established)
    {
        var context = Context() with { IsLocked = locked, HasManualCorrection = manual, EstablishedIdentity = established ? Id("old") : null };
        var result = SearchCandidateRanker.Rank(context, new[] { Film("new") });
        Assert.True(result.PreserveExisting);
        Assert.Null(result.SelectedIdentity);
        Assert.Empty(result.Candidates);
        Assert.Equal(CandidateDecision.Ambiguous, result.Decision);
    }

    [Fact]
    public void GeneratedHypothesis_IsNeverPromotedByYearAgreementAlone()
    {
        var queries = SearchCandidateGenerator.Generate(new("Blade.Runner.2049.1080p", UnifiedCategory.Film));
        var result = SearchCandidateRanker.Rank(Context() with { Queries = queries }, new[] { Film(title: "Blade Runner", year: 2049) });
        Assert.Equal(CandidateDecision.Ambiguous, result.Decision);
        Assert.Null(result.SelectedIdentity);
        Assert.Equal(1, Assert.Single(result.Candidates).QueryIndex);
        Assert.Equal(CandidateYearEvidence.Hypothesis, queries[1].YearEvidence);
    }

    [Fact]
    public void ExplicitSecondYear_UsesTheIntactNumericTitle()
    {
        var queries = SearchCandidateGenerator.Generate(new("Blade.Runner.2049.2017.1080p", UnifiedCategory.Film));
        var result = SearchCandidateRanker.Rank(Context() with { Queries = queries }, new[] { Film(title: "Blade Runner 2049", year: 2017) });
        Assert.Equal(CandidateDecision.Accept, result.Decision);
    }

    [Fact]
    public void EpisodeMetadata_IsCheckedIndependentlyOfExcellentTitleScore()
    {
        var context = Context("A Series") with { Category = UnifiedCategory.Serie, ExpectedMediaType = SearchMediaType.Series, Season = 1, Episode = 2 };
        var candidate = new ProviderSearchCandidate(Id("1", SearchMediaType.Series), "A Series", 2020, Season: 1, Episode: 2);
        Assert.Equal(CandidateDecision.Accept, SearchCandidateRanker.Rank(context, new[] { candidate }).Decision);
        var mismatch = SearchCandidateRanker.Rank(context, new[] { candidate with { Episode = 3 } });
        Assert.Equal(CandidateDecision.Reject, mismatch.Decision);
        Assert.Contains(Assert.Single(mismatch.Candidates).Contradictions, c => c.Code == "episode-mismatch" && c.IsStrong);
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(context, new[] { candidate with { Season = 2 } }).Decision);
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(context, new[] { candidate with { Episode = null } }).Decision);
    }

    [Fact]
    public void SameNumericId_InDifferentMediaScopesNeverMatches()
    {
        var context = Context() with { TrustedIdentityHint = Id("123") };
        var wrong = Film("123") with { Identity = Id("123", SearchMediaType.Series) };
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(context, new[] { wrong }).Decision);
    }

    [Fact]
    public void CrossProviderHint_NeedsAnExplicitKnownMapping()
    {
        var context = Context() with { TrustedIdentityHint = Id("123") };
        var otherProvider = Film() with { Identity = Id("123", provider: "other") };
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(context, new[] { otherProvider }).Decision);
        Assert.Equal(CandidateDecision.Accept, SearchCandidateRanker.Rank(context,
            new[] { otherProvider with { KnownExternalIdentities = new[] { Id("123") } } }).Decision);
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(context,
            new[] { otherProvider with { KnownExternalIdentities = new[] { Id("123"), Id("456") } } }).Decision);
    }

    [Fact]
    public void DuplicateHits_DoNotVoteOrCreateFalseCompetition()
    {
        var context = Context();
        var single = SearchCandidateRanker.Rank(context, new[] { Film() });
        var repeated = SearchCandidateRanker.Rank(context, new[] { Film(), Film() });
        Assert.Equal(single.Decision, repeated.Decision);
        Assert.Single(repeated.Candidates);
        Assert.Equal(single.Candidates[0].Confidence, repeated.Candidates[0].Confidence);
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(context, new[] { Film(), Film(year: 1990) }).Decision);
        var rejectedDuplicates = new[] { Film(year: 1990), Film(year: 1990) };
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(context, rejectedDuplicates).Decision);
    }

    [Fact]
    public void Competition_IsDeterministicAcrossInputOrderAndDowngradesAccepts()
    {
        var candidates = new[] { Film("2"), Film("1") };
        var a = SearchCandidateRanker.Rank(Context(), candidates);
        var b = SearchCandidateRanker.Rank(Context(), candidates.Reverse().ToArray());
        Assert.Equal(a.Candidates.Select(c => c.Identity), b.Candidates.Select(c => c.Identity));
        Assert.All(a.Candidates, c => Assert.Equal(CandidateDecision.Ambiguous, c.Decision));
        Assert.Null(a.SelectedIdentity);
    }

    [Fact]
    public void PolicyThresholds_AreConfigurableAndValidated()
    {
        var nearby = new[] { Film(year: 2021) };
        Assert.Equal(CandidateDecision.Accept, SearchCandidateRanker.Rank(Context(), nearby).Decision);
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(Context(), nearby, new(ParsedYearTolerance: 0)).Decision);
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(Context(), nearby, new(AcceptConfidence: .99)).Decision);
        Assert.Throws<ArgumentOutOfRangeException>(() => SearchCandidateRanker.Rank(Context(), nearby, new(MinimumConfidenceGap: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SearchCandidateRanker.Rank(Context(), nearby, new(AcceptConfidence: double.NaN)));
    }

    [Fact]
    public void EmptyAndOversizedBatches_FailClosedWithoutTruncation()
    {
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(Context(), Array.Empty<ProviderSearchCandidate>()).Decision);
        var result = SearchCandidateRanker.Rank(Context(), new[] { Film(), Film("2") }, new(MaximumResults: 1));
        Assert.Equal(CandidateDecision.Reject, result.Decision);
        Assert.Empty(result.Candidates);
        Assert.Contains("provider-result-limit-exceeded", result.Reasons);
    }

    [Fact]
    public void StrongContradiction_CannotBeOverriddenByConfidence()
    {
        var result = SearchCandidateRanker.Rank(Context() with { ConfirmedYear = 1990, TrustedIdentityHint = Id("1") }, new[] { Film() });
        Assert.Equal(CandidateDecision.Reject, result.Decision);
        Assert.True(Assert.Single(result.Candidates).Confidence >= .9);
    }

    [Fact]
    public void MinimumGap_IsAnExplicitTestedDecisionBoundary()
    {
        var results = new[] { Film("1"), Film("2", year: 2021) };
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(Context(), results).Decision);
        var separated = SearchCandidateRanker.Rank(Context(), results, new(MinimumConfidenceGap: .03));
        Assert.Equal(CandidateDecision.Accept, separated.Decision);
        Assert.Equal(Id("1"), separated.SelectedIdentity);
    }

    [Fact]
    public void ApproximateTitleScore_DoesNotSilentlyBecomeConfirmedIdentity()
    {
        var context = Context("One Two Three Four Five Six Seven Eight Nine Ten");
        var candidate = Film(title: "One Two Three Four Five Six Seven Eight Nine Eleven");
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(context, new[] { candidate }).Decision);
        Assert.Equal(CandidateDecision.Accept, SearchCandidateRanker.Rank(context, new[] { candidate },
            new(RequireExactTitleForAcceptance: false)).Decision);
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(context, new[] { candidate },
            new(MinimumTitleSimilarity: .95)).Decision);
    }


    [Fact]
    public void NumericAndConfirmedYearPolicies_AreExplicitAndTested()
    {
        var numeric = Context("1917", 2019);
        Assert.Equal(CandidateDecision.Accept, SearchCandidateRanker.Rank(numeric, new[] { Film(title: "1917", year: 2019) },
            new(NumericTitlesRequireTrustedIdentity: false)).Decision);
        var nearby = new[] { Film(year: 2021) };
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(Context() with { ConfirmedYear = 2020 }, nearby).Decision);
        Assert.Equal(CandidateDecision.Accept, SearchCandidateRanker.Rank(Context() with { ConfirmedYear = 2020 }, nearby,
            new(ConfirmedYearTolerance: 1)).Decision);
    }

    [Fact]
    public void EstablishedIdentity_IsPreservedEvenWhenGeneratorReturnedNoQueries()
    {
        var result = SearchCandidateRanker.Rank(Context() with { Queries = Array.Empty<SearchCandidate>(), EstablishedIdentity = Id("kept") },
            new[] { Film() });
        Assert.True(result.PreserveExisting);
        Assert.Null(result.SelectedIdentity);
    }

    [Fact]
    public void ReturnedEvidenceAndRanking_CannotBeModified()
    {
        var result = SearchCandidateRanker.Rank(Context(), new[] { Film() });
        Assert.Throws<NotSupportedException>(() => ((IList<RankedSearchCandidate>)result.Candidates).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result.Candidates[0].PositiveReasons).Clear());
    }

    [Fact]
    public void BadEpisodeMetadata_CannotBeRescuedByTitleAndYear()
    {
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(Context(), new[] { Film() with { Episode = 1 } }).Decision);
        var context = Context() with { ExpectedMediaType = SearchMediaType.Series, Category = UnifiedCategory.Serie, Season = -1 };
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(context,
            new[] { Film() with { Identity = Id("1", SearchMediaType.Series), Season = -1 } }).Decision);
    }

}
