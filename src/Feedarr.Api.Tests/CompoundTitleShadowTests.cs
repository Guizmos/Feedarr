using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Xunit.Abstractions;

namespace Feedarr.Api.Tests;

public sealed class CompoundTitleShadowTests(ITestOutputHelper output)
{
    // User-proposed identities; provider titles below are explicit simulated evidence, not captured/independently validated identities.
    public static IEnumerable<object[]> Cases()
    {
        yield return new object[] { "Le.Festin.Nu.Naked.Lunch.1991", 1991, 2742, "Le Festin nu", "Naked Lunch" };
        yield return new object[] { "Mutant.Forbidden.World.1982", 1982, 42251, "Mutant", "Forbidden World" };
        yield return new object[] { "Epic.LA.BATAILLE.DU.ROYAUME.2013", 2013, 116711, "La Bataille du royaume", "Epic" };
        yield return new object[] { "Society.on.the.Moors.Topical.Budget.522.1.1921", 1921, 1173160,
            "Society on the Moors (Topical Budget 522-1)", "Society on the Moors (Topical Budget 522-1)" };
    }
    private static ProviderSearchCandidate Candidate(int id, int year, string localized, string original)
    {
        var identity = new SearchIdentity("tmdb", SearchMediaType.Movie, id.ToString());
        return new(identity, original, year, OfficialTitles: new[] {
            new OfficialProviderTitle(localized, identity, ProviderTitleKind.Localized, ProviderTitleSource.SearchTitle, "fr-FR"),
            new OfficialProviderTitle(original, identity, ProviderTitleKind.Original, ProviderTitleSource.SearchOriginalTitle, "en") });
    }
    [Theory]
    [MemberData(nameof(Cases))]
    public void BareBetaTitles_RemainAmbiguousWithoutPromotingYear(string raw, int year, int id, string localized, string original)
    {
        var queries = SearchCandidateGenerator.Generate(new(raw, UnifiedCategory.Film));
        var ranked = SearchCandidateRanker.Rank(new(queries, UnifiedCategory.Film, SearchMediaType.Movie), new[] { Candidate(id, year, localized, original) });
        Assert.NotEmpty(queries);
        Assert.Equal(localized, MatchingShadowDiagnostics.SafeText(localized));
        Assert.Null(MatchingShadowDiagnostics.RedactionReason(localized));
        Assert.Equal(CandidateDecision.Ambiguous, ranked.Decision);
        Assert.Null(ranked.SelectedIdentity);
        Assert.Null(queries[0].Year);
        Assert.Equal(CandidateYearEvidence.None, queries[0].YearEvidence);
        var candidate = Assert.Single(ranked.Candidates);
        Assert.Equal(year, candidate.TrailingYearHypothesis);
        Assert.Equal(.90, candidate.Confidence);
        Assert.Equal(id != 1173160, candidate.CompoundTitle is not null);
        output.WriteLine($"{raw} => {string.Join(" / ", queries.Select(q => $"title={q.Title}, year={q.Year}, evidence={q.YearEvidence}"))}; decision={ranked.Decision}; reasons={string.Join(",",ranked.Candidates.SelectMany(c=>c.Contradictions.Select(x=>x.Code)))}; localized-display={MatchingShadowDiagnostics.SafeText(localized)}");
    }
    private static CandidateRankingContext Context(string title = "Le Festin nu Naked Lunch", int year = 1991)
        => new(new[] { new SearchCandidate(title, year, CandidateYearEvidence.Parsed, SearchCandidateOrigin.Primary) }, UnifiedCategory.Film, SearchMediaType.Movie);

    [Theory]
    [MemberData(nameof(Cases))]
    public void BetaTitles_WithIndependentLexicalYearContext_AreAccepted(string raw, int year, int id, string localized, string original)
    {
        var queries = SearchCandidateGenerator.Generate(new(raw + ".BluRay", UnifiedCategory.Film));
        Assert.Equal(year, queries[0].Year);
        var ranked = SearchCandidateRanker.Rank(new(queries, UnifiedCategory.Film, SearchMediaType.Movie), new[] { Candidate(id, year, localized, original) });
        Assert.Equal(CandidateDecision.Accept, ranked.Decision);
        Assert.Equal(id.ToString(), ranked.SelectedIdentity!.Id);
        var row = Assert.Single(ranked.Candidates);
        Assert.Null(row.TrailingYearHypothesis);
        if (id != 1173160) Assert.Contains("official-composed-title-exact", row.PositiveReasons);
        output.WriteLine($"Lexically dated {raw}: {ranked.Decision}; {queries[0].Title} / {queries[0].Year}");
    }

    [Theory]
    [InlineData("Le Festin nu Naked Lunch")]
    [InlineData("Naked Lunch Le Festin nu")]
    public void EitherWholeOrderedOfficialPair_HasExplicitProvenance(string title)
    {
        var candidate = Candidate(2742, 1991, "Le Festin nu", "Naked Lunch");
        var result = SearchCandidateRanker.Rank(Context(title), new[] { candidate });
        Assert.Equal(CandidateDecision.Accept, result.Decision);
        var proof = Assert.Single(result.Candidates).CompoundTitle!;
        Assert.Equal(candidate.Identity, proof.First.Identity); Assert.Equal(candidate.Identity, proof.Second.Identity);
        Assert.Contains(ProviderTitleKind.Localized, new[] { proof.First.Kind, proof.Second.Kind });
        Assert.Contains(ProviderTitleKind.Original, new[] { proof.First.Kind, proof.Second.Kind });
    }

    [Theory]
    [InlineData("Le Festin nu Naked Lunch Bonus")]
    [InlineData("Naked Le Festin nu Lunch")]
    [InlineData("Le Festin nu Society")]
    [InlineData("Le Festin nu Naked Lunch Le Festin nu")]
    [InlineData("Le Festin nu Naked")]
    public void PartialReorderedArbitraryOrRepeatedText_IsRejected(string title)
    {
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(Context(title), new[] { Candidate(2742, 1991, "Le Festin nu", "Naked Lunch") }).Decision);
    }

    [Theory]
    [InlineData("other-identity")]
    [InlineData("other-type")]
    [InlineData("other-provider")]
    [InlineData("missing-localized")]
    [InlineData("untyped-aliases")]
    [InlineData("bad-provenance")]
    public void PairNeedsTwoOfficialNamesForSameTypedIdentity(string fault)
    {
        var candidate = Candidate(2742, 1991, "Le Festin nu", "Naked Lunch");
        var original = candidate.OfficialTitles![1]; var local = candidate.OfficialTitles[0];
        candidate = fault switch
        {
            "other-identity" => candidate with { OfficialTitles = new[] { original, local with { Identity = local.Identity with { Id = "999" } } } },
            "other-type" => candidate with { OfficialTitles = new[] { original, local with { Identity = local.Identity with { MediaType = SearchMediaType.Series } } } },
            "other-provider" => candidate with { OfficialTitles = new[] { original, local with { Identity = local.Identity with { Provider = "tvmaze" } } } },
            "missing-localized" => candidate with { OfficialTitles = new[] { original } },
            "untyped-aliases" => candidate with { OfficialTitles = null, AlternativeTitles = new[] { local.Title, original.Title } },
            _ => candidate with { OfficialTitles = new[] { original, local with { Source = ProviderTitleSource.AlternativeTitlesEndpoint } } }
        };
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(Context(), new[] { candidate }).Decision);
    }

    [Fact]
    public void ContainedNames_DoNotJustifyDuplicateTitlePrefixes()
    {
        var candidate = Candidate(1, 1991, "Epic La Bataille du royaume", "Epic");
        var duplicate = SearchCandidateRanker.Rank(Context("Epic Epic La Bataille du royaume"), new[] { candidate });
        Assert.NotEqual(CandidateDecision.Accept, duplicate.Decision);
        Assert.Null(Assert.Single(duplicate.Candidates).CompoundTitle);
        Assert.Equal(CandidateDecision.Accept, SearchCandidateRanker.Rank(Context("Epic La Bataille du royaume"), new[] { candidate }).Decision);
    }

    [Fact]
    public void CompleteLocalizedEpicTitle_IsASingleOfficialTitle_NotInventedComposition()
    {
        var candidate = Candidate(116711, 2013, "Epic : La Bataille du royaume", "Epic");
        var result = SearchCandidateRanker.Rank(Context("Epic LA BATAILLE DU ROYAUME", 2013), new[] { candidate });
        Assert.Equal(CandidateDecision.Accept, result.Decision);
        Assert.Null(Assert.Single(result.Candidates).CompoundTitle);
        Assert.Contains("official-localized-title-exact", result.Candidates[0].PositiveReasons);
    }

    [Theory]
    [InlineData("year", "parsed-year-mismatch")]
    [InlineData("type", "media-type-mismatch")]
    [InlineData("number", "title-number-mismatch")]
    public void CompoundProof_CannotEraseContradictions(string fault, string reason)
    {
        var candidate = Candidate(2742, 1991, "Le Festin nu", "Naked Lunch"); var context = Context();
        if (fault == "year") candidate = candidate with { Year = 2010 };
        if (fault == "type") context = context with { ExpectedMediaType = SearchMediaType.Series, Category = UnifiedCategory.Serie };
        if (fault == "number") context = Context("Le Festin nu Naked Lunch 2");
        var result = SearchCandidateRanker.Rank(context, new[] { candidate });
        Assert.Equal(CandidateDecision.Reject, result.Decision);
        Assert.Contains(Assert.Single(result.Candidates).Contradictions, c => c.Code == reason && c.IsStrong);
    }

    [Fact]
    public void RemakesCompetingProofsAndEpisodeConstraints_RemainProtected()
    {
        var original = Candidate(2742, 1991, "Le Festin nu", "Naked Lunch");
        var remake = Candidate(999, 2010, "Le Festin nu", "Naked Lunch");
        Assert.Equal(original.Identity, SearchCandidateRanker.Rank(Context(), new[] { original, remake }).SelectedIdentity);
        var close = SearchCandidateRanker.Rank(Context(), new[] { original, Candidate(999, 1991, "Le Festin nu", "Naked Lunch") });
        Assert.Equal(CandidateDecision.Ambiguous, close.Decision); Assert.Null(close.SelectedIdentity);
        var seriesIdentity = original.Identity with { MediaType = SearchMediaType.Series };
        var series = original with { Identity = seriesIdentity, Season = 1, Episode = 3,
            OfficialTitles = original.OfficialTitles!.Select(t => t with { Identity = seriesIdentity }).ToArray() };
        var context = Context() with { Category = UnifiedCategory.Serie, ExpectedMediaType = SearchMediaType.Series, Season = 1, Episode = 2 };
        var result = SearchCandidateRanker.Rank(context, new[] { series });
        Assert.Equal(CandidateDecision.Reject, result.Decision);
        Assert.Contains(Assert.Single(result.Candidates).Contradictions, c => c.Code == "episode-mismatch");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PersistentIdentityAndManualLocks_NeverYieldReplacement(bool manual)
    {
        var candidate = Candidate(2742, 1991, "Le Festin nu", "Naked Lunch");
        var context = manual ? Context() with { HasManualCorrection = true } : Context() with { EstablishedIdentity = candidate.Identity };
        var result = SearchCandidateRanker.Rank(context, new[] { candidate });
        Assert.True(result.PreserveExisting); Assert.Null(result.SelectedIdentity); Assert.Empty(result.Candidates);
    }

    [Theory]
    [InlineData("522.1", true)]
    [InlineData("522-1", true)]
    [InlineData("522 1", true)]
    [InlineData("522–1", true)]
    [InlineData("5221", false)]
    [InlineData("522.01", false)]
    [InlineData("0522.1", false)]
    [InlineData("522.2", false)]
    [InlineData("1.522", false)]
    [InlineData("522/1", false)]
    [InlineData("522:1", false)]
    [InlineData("522+1", false)]
    public void IssueNumberTypography_PreservesEveryDigitAndOrder(string number, bool accept)
    {
        const string official = "Society on the Moors (Topical Budget 522-1)";
        var result = SearchCandidateRanker.Rank(Context("Society on the Moors Topical Budget " + number, 1921), new[] { Candidate(1173160, 1921, official, official) });
        Assert.Equal(accept ? CandidateDecision.Accept : CandidateDecision.Reject, result.Decision);
    }

    [Fact]
    public void CataloguingTextMissingFromOfficialTitles_IsNotAnAllowedSuffix()
    {
        var candidate = Candidate(1173160, 1921, "Society on the Moors", "Society on the Moors");
        var queries = SearchCandidateGenerator.Generate(new("Society.on.the.Moors.Topical.Budget.522.1.1921.BluRay", UnifiedCategory.Film));
        var result = SearchCandidateRanker.Rank(new(queries, UnifiedCategory.Film, SearchMediaType.Movie), new[] { candidate });
        Assert.Equal(CandidateDecision.Reject, result.Decision);
        Assert.Null(result.SelectedIdentity);
    }

    [Theory]
    [InlineData("Secret Society", "credential_keyword")]
    [InlineData("Movie / Alternate", "path_or_separator")]
    [InlineData("Movie https://private.example/?token=CANARY", "url")]
    [InlineData("user@example.org", "email_or_at_sign")]
    [InlineData("Movie.tracker.fr", "domain_like_text")]
    public void RedactionRemainsStrictButExplainsWhy(string title, string reason)
    {
        Assert.Equal("[titre masqué : contenu sensible possible]", MatchingShadowDiagnostics.SafeText(title));
        Assert.Equal(reason, MatchingShadowDiagnostics.RedactionReason(title));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShadowAdapter_UsesOnlyReceivedOfficialRows_AndKeepsPartialCoverageNotEvaluable(bool complete)
    {
        var raw = "Le.Festin.Nu.Naked.Lunch.1991.BluRay";
        var state = new MatchingShadowState { Id = 1, RawTitle = raw, Category = "film" };
        var original = new Feedarr.Api.Services.Tmdb.TmdbClient.SearchResult(2742, "Naked Lunch", "Naked Lunch", null, "movie", 1991, "en");
        var localized = original with { Title = "Le Festin nu" };
        var batch = new MatchingShadowBatch("Le Festin nu Naked Lunch", 1991, new[] { original }, complete) {
            ObservedResults = new[] { new Feedarr.Api.Services.Tmdb.TmdbClient.ObservedSearchResult(original, null), new(localized, "fr-FR") } };
        var observed = MatchingShadowEvaluator.Observe(true, state, () => state, batch, new("Reject", null, null))!;
        if (!complete) { Assert.Equal("not_evaluable", observed.Status); Assert.Null(observed.V2); return; }
        Assert.Equal(CandidateDecision.Accept, observed.V2!.Decision);
        var projection = MatchingShadowDiagnostics.Project(observed, raw, batch, 1);
        Assert.Equal(2, Assert.Single(projection.Candidates).CompoundTitles!.Count);
        Assert.All(projection.Candidates[0].CompoundTitles!, t => Assert.NotNull(t.Source));
    }

}
