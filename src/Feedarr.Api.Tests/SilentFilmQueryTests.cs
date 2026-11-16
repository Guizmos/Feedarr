using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Tests;

public sealed class SilentFilmQueryTests
{
    [Theory]
    [InlineData("Les.Roses.magiques.1906.MUET.VFF.SD.DVDRip.AAC.2.0.x264-NOTAG", "Les Roses magiques", 1906)]
    [InlineData("Le.Fakir.de.Singapour.1908.MUET.VFF.SD.DVDRip.AAC.2.0.x264-NOTAG", "Le Fakir de Singapour", 1908)]
    [InlineData("Example.2023.MUET.VO.1080p.WEB.AAC.2.0.H264-GRP", "Example", 2023)]
    [InlineData("Example.1995.CUSTOM.MUET.VOSTFR.480p.DVD.REMUX.AC3.2.0.MPEG2-GRP", "Example", 1995)]
    public void TechnicalSilentMarkerDoesNotPolluteQueryOrLoseYear(string raw, string title, int year)
    {
        var parsed = new TitleParser().Parse(raw, UnifiedCategory.Film);
        Assert.Equal(title, parsed.TitleClean); Assert.Equal(year, parsed.Year);
        var query = Assert.Single(SearchCandidateGenerator.Generate(new(raw, UnifiedCategory.Film)));
        Assert.Equal(title, query.Title); Assert.Equal(year, query.Year);
        Assert.Equal(CandidateYearEvidence.Parsed, query.YearEvidence);
    }

    [Theory]
    [InlineData("Le.Muet.1998.VFF.DVDRip.x264-GRP", "Le Muet", 1998)]
    [InlineData("1917.2019.VFF.BluRay.x264-GRP", "1917", 2019)]
    [InlineData("MUET.2020.VFF.WEB.x264-GRP", "MUET", 2020)]
    [InlineData("Example.1906.MUET", "Example 1906 MUET", null)]
    public void TitleWordsAndUnsupportedBareSuffixArePreserved(string raw, string title, int? year)
    {
        var parsed = new TitleParser().Parse(raw, UnifiedCategory.Film);
        Assert.Equal(title, parsed.TitleClean); Assert.Equal(year, parsed.Year);
        Assert.Equal(title, SearchCandidateGenerator.Generate(new(raw, UnifiedCategory.Film))[0].Title);
    }

    [Fact]
    public void FrenchOfficialTitleSupportsDifferentOriginalWithoutBypassingCompetitorsOrYear()
    {
        var queries = SearchCandidateGenerator.Generate(new("Un.Titre.francais.1906.MUET.VFF.DVDRip.x264-GRP", UnifiedCategory.Film));
        var id = new SearchIdentity("tmdb", SearchMediaType.Movie, "1");
        var candidate = new ProviderSearchCandidate(id, "Different Original", 1906, OfficialTitles: new[] {
            new OfficialProviderTitle("Un Titre francais", id, ProviderTitleKind.Localized, ProviderTitleSource.SearchTitle, "fr-FR") });
        var context = new CandidateRankingContext(queries, UnifiedCategory.Film, SearchMediaType.Movie);
        Assert.Equal(CandidateDecision.Accept, SearchCandidateRanker.Rank(context, new[] { candidate }).Decision);
        Assert.NotEqual(CandidateDecision.Accept, SearchCandidateRanker.Rank(context, new[] { candidate with { Year = 2006 } }).Decision);
        var other = new SearchIdentity("tmdb", SearchMediaType.Movie, "2");
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(context, new[] { candidate,
            candidate with { Identity = other, Title = "Un Titre francais", OfficialTitles = null } }).Decision);
    }
}
