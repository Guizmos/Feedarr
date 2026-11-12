using System.Globalization;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Tests;

public sealed class SearchCandidateGeneratorTests
{
    [Fact]
    public void ResolutionOnlyAmbiguity_PreservesPrimaryAndLabelsTheAlternative()
    {
        var result = SearchCandidateGenerator.Generate(new("Blade.Runner.2049.1080p", UnifiedCategory.Film));
        Assert.Collection(result,
            c => Assert.Equal(new SearchCandidate("Blade Runner 2049", null, CandidateYearEvidence.None, SearchCandidateOrigin.Primary), c),
            c => Assert.Equal(new SearchCandidate("Blade Runner", 2049, CandidateYearEvidence.Hypothesis, SearchCandidateOrigin.AmbiguousTrailingNumber), c));
    }

    [Theory]
    [InlineData("Blade.Runner.2049.2017.1080p", "Blade Runner 2049", 2017)]
    [InlineData("Argo.2012.DTS.1080p.BluRay-GRP", "Argo", 2012)]
    [InlineData("Film.(2020).1080p", "Film", 2020)]
    public void ParsedYear_IsNotReinterpretedOrCalledConfirmed(string raw, string title, int year)
    {
        var candidate = Assert.Single(SearchCandidateGenerator.Generate(new(raw, UnifiedCategory.Film)));
        Assert.Equal(new SearchCandidate(title, year, CandidateYearEvidence.Parsed, SearchCandidateOrigin.Primary), candidate);
    }

    [Theory]
    [InlineData("Cyberpunk.2077", "Cyberpunk 2077")]
    [InlineData("Cyberpunk.2077.v2.5.WIN.PREACTIVED-TEAM", "Cyberpunk 2077")]
    [InlineData("F1.2026.Update.v1.2-CODEX", "F1 2026")]
    [InlineData("Game.Remastered-CODEX", "Game Remastered")]
    public void GameNumbersAndEditions_StayInThePrimary(string raw, string title)
    {
        var candidate = Assert.Single(SearchCandidateGenerator.Generate(new(raw, UnifiedCategory.JeuWindows)));
        Assert.Equal(title, candidate.Title);
        Assert.Null(candidate.Year);
        Assert.Equal(CandidateYearEvidence.None, candidate.YearEvidence);
    }

    [Theory]
    [InlineData("Cyberpunk.2077")]
    [InlineData("Space.1999")]
    [InlineData("1917.1080p")]
    [InlineData("2001.1080p")]
    [InlineData("1999.2000.1080p")]
    public void BareTitlesOrMissingText_DoNotInventAnAlternative(string raw)
        => Assert.Single(SearchCandidateGenerator.Generate(new(raw, UnifiedCategory.Film)));

    [Theory]
    [InlineData(UnifiedCategory.Serie)]
    [InlineData(UnifiedCategory.Anime)]
    [InlineData(UnifiedCategory.Animation)]
    [InlineData(UnifiedCategory.Emission)]
    [InlineData(UnifiedCategory.Spectacle)]
    public void NonMovieCategories_DoNotInheritMovieHypotheses(UnifiedCategory category)
        => Assert.Single(SearchCandidateGenerator.Generate(new("Show.2049.1080p", category)));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EstablishedIdentityOrManualCorrection_BlocksAllProposals(bool identity, bool manual)
        => Assert.Empty(SearchCandidateGenerator.Generate(new("Blade.Runner.2049.1080p", UnifiedCategory.Film, identity, manual)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingInput_ReturnsNoCandidates(string? raw)
        => Assert.Empty(SearchCandidateGenerator.Generate(new(raw, UnifiedCategory.Film)));

    [Fact]
    public void Generation_IsBoundedDeterministicAndIndependentOfCulture()
    {
        var input = new SearchCandidateInput("Title.1999.2049.1080p", UnifiedCategory.Film);
        var expected = SearchCandidateGenerator.Generate(input).ToArray();
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "fr-FR", "en-US", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                var actual = SearchCandidateGenerator.Generate(input);
                Assert.InRange(actual.Count, 1, SearchCandidateGenerator.MaxCandidates);
                Assert.Equal(expected, actual.ToArray());
                Assert.Equal(SearchCandidateOrigin.Primary, actual[0].Origin);
                Assert.Equal(actual.Count, actual.Distinct().Count());
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void ReturnedCollection_CannotBeMutated()
    {
        var result = SearchCandidateGenerator.Generate(new("Film.2049.1080p", UnifiedCategory.Film));
        Assert.Throws<NotSupportedException>(() => ((IList<SearchCandidate>)result)[0] = result[1]);
    }
}
