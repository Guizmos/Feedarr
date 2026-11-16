using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Tests;

// Frozen simulated comparison corpus. These rows do not assert provider search coverage.
public sealed class TitleTypographyCorpusTests
{
    private static readonly SearchIdentity Id = new("tmdb", SearchMediaType.Movie, "16577");
    private static CandidateRankingContext Context(string title, int year = 2009) => new(
        new[] { new SearchCandidate(title, year, CandidateYearEvidence.Parsed, SearchCandidateOrigin.Primary) },
        UnifiedCategory.Film, SearchMediaType.Movie);
    private static ProviderSearchCandidate Candidate(string title) => new(Id, title, 2009);

    [Theory]
    [InlineData("Astroboy", "Astro Boy", false)]
    [InlineData("Spider-Man", "Spider Man", true)]
    [InlineData("Cafe\u0301", "Café", true)]
    [InlineData("L’été", "L'ete", true)]
    [InlineData("A  Simple   Plan", "A Simple Plan", true)]
    [InlineData("Therapist", "The Rapist", false)]
    [InlineData("Nowhere", "Now Here", false)]
    [InlineData("Man Bites Dog", "Dog Bites Man", false)]
    public void CurrentBoundaryPolicyIsExplicit(string query, string title, bool accepted)
    {
        var result = SearchCandidateRanker.Rank(Context(query), new[] { Candidate(title) });
        Assert.Equal(accepted, result.Decision == CandidateDecision.Accept);
    }

    [Fact]
    public void ProviderOwnedAliasResolvesSpacingWithoutInventingAnAlias()
    {
        var candidate = Candidate("Astro Boy") with { OfficialTitles = new[] {
            new OfficialProviderTitle("Astroboy", Id, ProviderTitleKind.Alternative, ProviderTitleSource.AlternativeTitlesEndpoint) } };
        Assert.Equal(CandidateDecision.Accept, SearchCandidateRanker.Rank(Context("Astroboy"), new[] { candidate }).Decision);
        Assert.NotEqual(CandidateDecision.Accept, SearchCandidateRanker.Rank(Context("Astroboy", 1980), new[] { candidate }).Decision);
        Assert.NotEqual(CandidateDecision.Accept, SearchCandidateRanker.Rank(Context("Astroboy"), new[] {
            candidate with { Identity = new("tmdb", SearchMediaType.Movie, "999") } }).Decision);
        Assert.NotEqual(CandidateDecision.Accept, SearchCandidateRanker.Rank(Context("Astroboy"), new[] {
            candidate with { Identity = new("tmdb", SearchMediaType.Series, "16577") } }).Decision);
    }
}
