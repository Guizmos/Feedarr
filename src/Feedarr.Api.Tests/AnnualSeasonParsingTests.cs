using Feedarr.Api.Models;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Tests;

public sealed class AnnualSeasonParsingTests
{
    [Theory]
    [InlineData(UnifiedCategory.Serie)]
    [InlineData(UnifiedCategory.Emission)]
    public void AnnualSeasonDoesNotBecomeCreationYearOrTruncatedSeason(UnifiedCategory category)
    {
        var parsed = new TitleParser().Parse("Example.Show.S2026E12.VOF.1080p.WEB.H264-GRP", category);
        Assert.Equal("Example Show", parsed.TitleClean);
        Assert.Equal(2026, parsed.Season);
        Assert.Equal(12, parsed.Episode);
        Assert.Null(parsed.Year);
        var queries = SearchCandidateGenerator.Generate(new("Example.Show.S2026E12.VOF.1080p.WEB.H264-GRP", category));
        Assert.Equal("Example Show", queries[0].Title);
        Assert.Null(queries[0].Year);
    }

    [Theory]
    [InlineData("Example.S20E12.1080p", 20, 12)]
    [InlineData("Example.S02.1080p", 2, null)]
    [InlineData("Example.S20261E12.1080p", null, null)]
    public void OrdinaryAndMalformedMarkersRemainDistinct(string raw, int? season, int? episode)
    {
        var parsed = new TitleParser().Parse(raw, UnifiedCategory.Serie);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episode, parsed.Episode);
    }
    [Fact]
    public void AnnualSeasonRequiresEpisodeProofWithoutContradictingCreationYear()
    {
        var id = new SearchIdentity("tmdb", SearchMediaType.Series, "1");
        var query = new SearchCandidate("Example Show", null, CandidateYearEvidence.None, SearchCandidateOrigin.Primary);
        var context = new CandidateRankingContext(new[] { query }, UnifiedCategory.Emission, SearchMediaType.Series, Season: 2026, Episode: 12);
        var candidate = new ProviderSearchCandidate(id, "Example Show", 2006);
        Assert.NotEqual(CandidateDecision.Accept, SearchCandidateRanker.Rank(context, new[] { candidate }).Decision);
        var verified = candidate with { Season = 2026, Episode = 12, Television = new(id, true, true) };
        Assert.Equal(CandidateDecision.Accept, SearchCandidateRanker.Rank(context, new[] { verified }).Decision);
    }
}
