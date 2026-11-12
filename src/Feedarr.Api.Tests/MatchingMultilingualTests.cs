using System.Diagnostics;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Tmdb;
using Xunit.Abstractions;

namespace Feedarr.Api.Tests;

public sealed class MatchingMultilingualTests(ITestOutputHelper output)
{
    // Beta proposals supplied by the user, NOT independently validated provider identities.
    // Localized Search responses below are explicit simulated fixtures, not downloaded captures.
    public static IEnumerable<object[]> BetaProposals()
    {
        yield return new object[] { "Le Robot sauvage", 2024, 1184918, "The Wild Robot" };
        yield return new object[] { "Le Magicien d'Oz", 1939, 630, "The Wizard of Oz" };
        yield return new object[] { "Massacre à la tronçonneuse", 1974, 30497, "The Texas Chain Saw Massacre" };
        yield return new object[] { "Ghost Rider : L'Esprit de vengeance", 2011, 71676, "Ghost Rider: Spirit of Vengeance" };
        yield return new object[] { "L'Emprise des ténèbres", 1988, 11503, "The Serpent and the Rainbow" };
        yield return new object[] { "Coup de foudre pour le fils du Père Noël", 2023, 1180796, "Catch Me If You Claus" };
        yield return new object[] { "Paradis express", 1997, 158, "Knockin' on Heaven's Door" };
    }
    private static SearchIdentity Identity(int id = 100, SearchMediaType type = SearchMediaType.Movie) => new("tmdb", type, id.ToString());
    private static CandidateRankingContext Context(string title = "Le Robot sauvage", int year = 2024)
        => new(new[] { new SearchCandidate(title, year, CandidateYearEvidence.Parsed, SearchCandidateOrigin.Primary) }, UnifiedCategory.Film, SearchMediaType.Movie);
    private static ProviderSearchCandidate Candidate(string title = "The Wild Robot", int year = 2024, string official = "Le Robot sauvage")
        => new(Identity(), title, year, OfficialTitles: new[] { new OfficialProviderTitle(official, Identity(), ProviderTitleKind.Localized, ProviderTitleSource.SearchTitle, "fr-FR") });
    private static MatchingShadowState State(string title, int year) => new() { Id = 1, Category = "film", RawTitle = $"{title}.{year}.BluRay.x264-GRP", TitleClean = title, Year = year };
    private static TmdbClient.ObservedSearchResult Row(int id, string title, string original, int year, string? language = null, string type = "movie")
        => new(new(id, title, original, null, type, year, "en"), language);
    private static MatchingShadowObservation Observe(MatchingShadowState state, params TmdbClient.ObservedSearchResult[] rows)
        => MatchingShadowEvaluator.Observe(true, state, () => state,
            new(state.TitleClean!, state.Year, rows.Take(1).Select(r => r.Result).ToArray(), true) { ObservedResults = rows },
            new("Accept", Identity(rows.FirstOrDefault()?.Result.TmdbId ?? 1), 1))!;

    [Theory]
    [MemberData(nameof(BetaProposals))]
    public void BetaProposals_RecoverOnlyWhenLocalizedProviderEvidenceIsPresent(string french, int year, int id, string english)
    {
        var state = State(french, year);
        var englishRow = Row(id, english, english, year);
        var before = SearchCandidateRanker.Rank(Context(french, year), new[] { new ProviderSearchCandidate(Identity(id), english, year, new[] { english }) });
        Assert.NotEqual(CandidateDecision.Accept, before.Decision);
        var missingTranslation = Observe(state, englishRow);
        Assert.NotEqual(CandidateDecision.Accept, missingTranslation.V2!.Decision);
        var after = Observe(state, englishRow, Row(id, french, english, year, "fr-FR"));
        Assert.Equal("evaluated", after.Status);
        Assert.Equal(CandidateDecision.Accept, after.V2!.Decision);
        Assert.Equal(Identity(id), after.V2.SelectedIdentity);
        Assert.Contains("official-localized-title-exact", Assert.Single(after.V2.Candidates).PositiveReasons);
        output.WriteLine($"{french}: baseline={before.Decision}; English-only={missingTranslation.V2.Decision}; localized-evidence={after.V2.Decision}");
    }

    [Fact]
    public void OriginalAndSeveralOfficialTitles_AreRecognizedWithoutBecomingIndependentVotes()
    {
        var identity = Identity();
        var titles = new[] {
            new OfficialProviderTitle("Le Robot sauvage", identity, ProviderTitleKind.Localized, ProviderTitleSource.SearchTitle, "fr-FR"),
            new OfficialProviderTitle("The Wild Robot", identity, ProviderTitleKind.Original, ProviderTitleSource.SearchOriginalTitle, "en"),
            new OfficialProviderTitle("Robot des bois", identity, ProviderTitleKind.Alternative, ProviderTitleSource.AlternativeTitlesEndpoint, "fr") };
        var candidate = Candidate(title: "Different display") with { OfficialTitles = titles };
        foreach (var title in titles)
        {
            var result = SearchCandidateRanker.Rank(Context(title.Title), new[] { candidate, candidate });
            Assert.Equal(CandidateDecision.Accept, result.Decision);
            Assert.Single(result.Candidates);
            Assert.Contains(result.Candidates[0].PositiveReasons, r => r.StartsWith("official-"));
        }
    }

    [Fact]
    public void ApproximateOrUnverifiedTranslation_DoesNotBecomeOfficialEvidence()
    {
        var result = SearchCandidateRanker.Rank(Context(), new[] { Candidate(official: "Le Robot très sauvage") });
        Assert.NotEqual(CandidateDecision.Accept, result.Decision);
        Assert.DoesNotContain(Assert.Single(result.Candidates).PositiveReasons, r => r.StartsWith("official-"));
        var unverified = Candidate() with { OfficialTitles = null };
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(Context(), new[] { unverified }).Decision);
        var wrongIdentity = Candidate() with { OfficialTitles = new[] { Candidate().OfficialTitles![0] with { Identity = Identity(999) } } };
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(Context(), new[] { wrongIdentity }).Decision);
        var fabricatedAlternative = Candidate() with { OfficialTitles = new[] { Candidate().OfficialTitles![0] with { Kind = ProviderTitleKind.Alternative } } };
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(Context(), new[] { fabricatedAlternative }).Decision);
    }

    [Theory]
    [InlineData("year", "parsed-year-mismatch")]
    [InlineData("type", "media-type-mismatch")]
    [InlineData("number", "title-number-mismatch")]
    [InlineData("episode", "episode-mismatch")]
    public void OfficialTitle_CannotOverrideContradictions(string kind, string reason)
    {
        var context = Context();
        var candidate = Candidate();
        if (kind == "year") candidate = candidate with { Year = 1990 };
        if (kind == "type") candidate = candidate with { Identity = Identity(type: SearchMediaType.Series) };
        if (kind == "number") context = Context("Le Robot sauvage 2");
        if (kind == "episode")
        {
            context = context with { Category = UnifiedCategory.Serie, ExpectedMediaType = SearchMediaType.Series, Season = 1, Episode = 2 };
            candidate = candidate with { Identity = Identity(type: SearchMediaType.Series), Title = "Le Robot sauvage", Season = 1, Episode = 3 };
        }
        var result = SearchCandidateRanker.Rank(context, new[] { candidate });
        Assert.Equal(CandidateDecision.Reject, result.Decision);
        Assert.Contains(Assert.Single(result.Candidates).Contradictions, c => c.Code == reason && c.IsStrong);
    }

    [Fact]
    public void RemakeAndCloseCompetitors_AreNotHiddenByLocalizedTitle()
    {
        var same = Candidate();
        var remake = same with { Identity = Identity(200), Year = 2000, Title = "Le Robot sauvage" };
        var result = SearchCandidateRanker.Rank(Context(), new[] { same, remake });
        Assert.Equal(Identity(), result.SelectedIdentity);
        var close = SearchCandidateRanker.Rank(Context(), new[] { same, same with { Identity = Identity(200), Title = "Le Robot sauvage" } });
        Assert.Equal(CandidateDecision.Ambiguous, close.Decision);
        Assert.Null(close.SelectedIdentity);
    }

    [Fact]
    public void ConflictingProviderYears_RemainAmbiguousAfterTitleAggregation()
    {
        var result = Observe(State("Le Robot sauvage", 2024), Row(100, "The Wild Robot", "The Wild Robot", 2024),
            Row(100, "Le Robot sauvage", "The Wild Robot", 1990, "fr-FR"));
        Assert.Equal(CandidateDecision.Ambiguous, result.V2!.Decision);
        Assert.Contains(Assert.Single(result.V2.Candidates).Contradictions, c => c.Code == "conflicting-provider-records");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialCoverageAndManualProtection_RemainNotEvaluable(bool manual)
    {
        var state = State("Le Robot sauvage", 2024) with { ManualProvider = manual ? "tmdb" : null };
        var result = MatchingShadowEvaluator.Observe(true, state, () => state,
            new(state.TitleClean!, 2024, new[] { Row(100, "Le Robot sauvage", "The Wild Robot", 2024).Result }, false), new("Reject", null, null))!;
        Assert.Equal("not_evaluable", result.Status);
        Assert.Null(result.V2);
    }

    [Fact]
    public async Task LiveCapture_PreservesLocalizedRowsWithoutChangingHistoricalQueriesOrSelection()
    {
        using var off = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true, localizedMovieTitle: "La Matrice");
        using var on = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, matchingShadowEnabled: true,
            completeTmdbMetadata: true, localizedMovieTitle: "La Matrice");
        var offId = off.IngestRelease("La.Matrice.1999.BluRay.x264-GRP", UnifiedCategory.Film);
        var onId = on.IngestRelease("La.Matrice.1999.BluRay.x264-GRP", UnifiedCategory.Film);
        var oldResult = await off.FetchAsync(offId);
        var newResult = await on.FetchAsync(onId);
        Assert.Equal(oldResult.Ok, newResult.Ok);
        Assert.Equal(oldResult.StatusCode, newResult.StatusCode);
        Assert.Equal(off.TmdbSearchRequests.Select(u => u.AbsoluteUri), on.TmdbSearchRequests.Select(u => u.AbsoluteUri));
        Assert.Equal(off.GetReleaseForPoster(offId)!.TmdbId, on.GetReleaseForPoster(onId)!.TmdbId);
        var observation = Assert.Single(on.ShadowObservations);
        Assert.Equal(CandidateDecision.Accept, observation.V2!.Decision);
        Assert.Contains("official-localized-title-exact", Assert.Single(observation.V2.Candidates).PositiveReasons);
        Assert.Contains(Assert.Single(on.ShadowDiagnostics.Snapshot().Entries).Candidates[0].OfficialTitles!, t => t.Title == "La Matrice" && t.Kind == "Localized");
    }

    [Fact]
    public void SameNumericId_CannotBorrowLocalizedTitleFromOtherTmdbType()
    {
        var result = Observe(State("Le Robot sauvage", 2024), Row(100, "The Wild Robot", "The Wild Robot", 2024),
            Row(100, "Le Robot sauvage", "Different Series", 2024, "fr-FR", "tv"));
        Assert.Equal(CandidateDecision.Reject, result.V2!.Decision);
        Assert.Null(result.V2.SelectedIdentity);
        Assert.Equal(2, result.V2.Candidates.Count);
    }

    [Fact]
    public void RawCapture_IsBounded_AndOverflowInvalidatesCoverage()
    {
        using var capture = new TmdbClient.MatchingShadowCapture(() => { });
        capture.Expect(); capture.Observe(true); capture.Observe(true);
        for (var i = 0; i < 101; i++) capture.Add(Row(i + 1, "A Title", "A Title", 2024));
        Assert.Equal(100, capture.Results.Count);
        Assert.False(capture.IsComplete);
    }

    [Fact]
    public void LocalCostComparison_UsesIdenticalFixedFixtures_NoNetworkOrDatabase()
    {
        var context = Context();
        var baseline = Candidate() with { OfficialTitles = null };
        var multilingual = Candidate();
        for (var i = 0; i < 100; i++) { SearchCandidateRanker.Rank(context, new[] { baseline }); SearchCandidateRanker.Rank(context, new[] { multilingual }); }
        const int iterations = 2000;
        (double time, long bytes) Measure(ProviderSearchCandidate candidate)
        {
            var startBytes = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++) SearchCandidateRanker.Rank(context, new[] { candidate });
            return (watch.Elapsed.TotalMilliseconds * 1000 / iterations, (GC.GetAllocatedBytesForCurrentThread() - startBytes) / iterations);
        }
        var before = Measure(baseline); var after = Measure(multilingual);
        output.WriteLine($"Fixed ranker fixture, {iterations} iterations: legacy fields {before.time:F2} us / {before.bytes} B; official titles {after.time:F2} us / {after.bytes} B. Not a production benchmark.");
    }
}
