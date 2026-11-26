using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Matching.Replay;

namespace Feedarr.Api.Tests;

public sealed class MatchingP7ContextualTests
{
    private static readonly CandidateRankingPolicy Final = new(ContextualVideoDecision: true);
    private static CandidateRankingContext Context(string title = "A Very Distinct Original Film", int? year = 2020) =>
        new(new[] { new SearchCandidate(title, year, year.HasValue ? CandidateYearEvidence.Parsed : CandidateYearEvidence.None, SearchCandidateOrigin.Primary) }, UnifiedCategory.Film, SearchMediaType.Movie);
    private static ProviderSearchCandidate Movie(string title = "A Very Distinct Original Movie", int year = 2020, string id = "1")
    {
        var key = new SearchIdentity("tmdb", SearchMediaType.Movie, id);
        return new(key, title, year, OfficialTitles: new[] { new OfficialProviderTitle(title, key, ProviderTitleKind.Original, ProviderTitleSource.SearchOriginalTitle) });
    }
    [Fact]
    public void StrongYearAndTypeRelaxFinalDecisionOnly()
    {
        var context = Context("The Very Distinct Original Film Extra");
        var pool = new[] { Movie("The Very Distinct Original Film") };
        Assert.NotEqual(CandidateDecision.Accept, SearchCandidateRanker.Rank(context, pool).Decision);
        var result = SearchCandidateRanker.Rank(context, pool, Final);
        Assert.Equal(CandidateDecision.Accept, result.Decision);
        Assert.Contains("p7-contextual-video", Assert.Single(result.Candidates).PositiveReasons);
        Assert.NotEqual(CandidateDecision.Accept, SearchCandidateRanker.Rank(context with { Queries = new[] { context.Queries[0] with { Year = null, YearEvidence = CandidateYearEvidence.None } } }, pool, Final).Decision);
        Assert.NotEqual(CandidateDecision.Accept, SearchCandidateRanker.Rank(context, new[] { pool[0] with { OfficialTitles = null } }, Final).Decision);
        Assert.NotEqual(CandidateDecision.Accept, SearchCandidateRanker.Rank(context, new[] { pool[0] with { Year = 2021 } }, Final).Decision);
    }
    [Theory]
    [InlineData("Bruised", 2020)]
    [InlineData("Whistle", 2025)]
    [InlineData("Oppenheimer", 2023)]
    public void ExactNeighborGapNeverBypassesP6A(string title, int year)
    {
        var result = SearchCandidateRanker.Rank(Context(title, year), new[] { Movie(title, year), Movie(title, year + 1, "2") }, Final);
        Assert.Equal(CandidateDecision.Ambiguous, result.Decision);
    }
    [Fact]
    public void ExactIdentityAndManualProtectionRemainAuthoritative()
    {
        var c = Movie();
        foreach (var context in new[] { Context() with { HasManualCorrection = true }, Context() with { IsLocked = true }, Context() with { EstablishedIdentity = c.Identity } })
            Assert.True(SearchCandidateRanker.Rank(context, new[] { c }, Final).PreserveExisting);
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(Context() with { TrustedIdentityHint = c.Identity with { Id = "other" } }, new[] { c }, Final).Decision);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProgrammeScopeRequiresExactOfficialCrossCatalogueIdentity(bool corroborated)
    {
        var key = new SearchIdentity("tmdb", SearchMediaType.Series, "1");
        var other = new SearchIdentity("tvmaze", SearchMediaType.Series, "2");
        var queries = new[] { new SearchCandidate("Programme", null, CandidateYearEvidence.None, SearchCandidateOrigin.ProgrammeTitleHypothesis) };
        var context = new CandidateRankingContext(queries, UnifiedCategory.Emission, SearchMediaType.Series);
        var c = new ProviderSearchCandidate(key, "Programme", OfficialTitles: new[] { new OfficialProviderTitle("Programme", key, ProviderTitleKind.Localized, ProviderTitleSource.SearchTitle, "fr-FR") },
            Television: new(key, null, null, CorroboratingIdentity: corroborated ? other : null), KnownExternalIdentities: new[] { other });
        Assert.Equal(corroborated ? CandidateDecision.Accept : CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(context, new[] { c }, Final).Decision);
        Assert.NotEqual(CandidateDecision.Accept, SearchCandidateRanker.Rank(context with { Season = 1, Episode = 2 }, new[] { c }, Final).Decision);
    }
    [Fact]
    public void MissingTvProofStillCannotAcceptAtPointNine()
    {
        var key = new SearchIdentity("tmdb", SearchMediaType.Series, "1");
        var context = new CandidateRankingContext(new[] { new SearchCandidate("FBI", null, CandidateYearEvidence.None, SearchCandidateOrigin.Primary) }, UnifiedCategory.Serie, SearchMediaType.Series, Season: 8, Episode: 5);
        var c = new ProviderSearchCandidate(key, "FBI", 2018, Season: 8, Episode: 5, OfficialTitles: new[] { new OfficialProviderTitle("FBI", key, ProviderTitleKind.Original, ProviderTitleSource.SearchOriginalTitle) });
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(context, new[] { c }, Final).Decision);
    }
    [Fact]
    public void ContextualCorpusRoundTripsAndLegacyPayloadKeepsOldPolicy()
    {
        var context = Context("The Very Distinct Original Film Extra"); var candidates = new[] { Movie("The Very Distinct Original Film") };
        var result = SearchCandidateRanker.Rank(context, candidates, Final);
        var observation = new MatchingShadowObservation(1, "evaluated", new("unknown", null, null), result, [], false) {
            RankingContext = context, RankingPolicy = Final, Queries = context.Queries, ProviderCandidates = candidates };
        Assert.Equal(CandidateDecision.Accept, RankingCorpus.Replay(RankingCorpus.Create(observation)!.Value).Decision);
        var legacy = observation with { RankingPolicy = null };
        Assert.NotEqual(CandidateDecision.Accept, RankingCorpus.Replay(RankingCorpus.Create(legacy)!.Value).Decision);
    }
}
