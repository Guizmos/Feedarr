using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Tests;

public sealed class TitleParserLocalOptimizationTests
{
    public static IEnumerable<object[]> LocalFilms()
    {
        yield return new object[] { "La.Famille.Addams.1991.Extented.Cut.MULTI.VFI.1080p.BluRay.HDLight.HDR10.10bit.AC3.5.1.x265-DreadTeam", "La Famille Addams", 1991, "BluRay", "x265", "DreadTeam" };
        yield return new object[] { "Anomie.2026.Tamil.VOSTFR.2026.1080p.WEBRip.AAC.2.0.x264-notag", "Anomie", 2026, "WEBRip", "x264", "notag" };
        yield return new object[] { "Scanners.1981.RM.MULTI.VFF.1080p.BluRay.EAC3.5.1.x265-DreadTeam", "Scanners", 1981, "BluRay", "x265", "DreadTeam" };
    }

    [Theory]
    [MemberData(nameof(LocalFilms))]
    public void RealFilmSuffixesProduceStructuredMetadataAndExactQueries(string raw, string title, int year, string source, string codec, string group)
    {
        var original = raw;
        var parsed = new TitleParser().Parse(raw, UnifiedCategory.Film);
        Assert.Equal(title, parsed.TitleClean);
        Assert.Equal(year, parsed.Year);
        Assert.Equal("1080p", parsed.Resolution);
        Assert.Equal(source, parsed.Source);
        Assert.Equal(codec, parsed.Codec);
        Assert.Equal(group, parsed.ReleaseGroup);
        Assert.Equal(original, raw);
        var query = Assert.Single(SearchCandidateGenerator.Generate(new(raw, UnifiedCategory.Film)));
        Assert.Equal(new SearchCandidate(title, year, CandidateYearEvidence.Parsed, SearchCandidateOrigin.Primary), query);
        var candidate = new ProviderSearchCandidate(new("tmdb", SearchMediaType.Movie, "123"), title, year);
        Assert.Equal(CandidateDecision.Accept, SearchCandidateRanker.Rank(new(new[] { query }, UnifiedCategory.Film, SearchMediaType.Movie), new[] { candidate }).Decision);
    }

    [Theory]
    [InlineData("Film.1991.Extended.Cut.MULTI.1080p.BluRay.x265-TEAM", "Film", 1991)]
    [InlineData("Anomie.2026.Tamil.VOSTFR.1080p.WEBRip.x264-TEAM", "Anomie", 2026)]
    [InlineData("The.Extended.Cut.2020.MULTI.1080p.WEB.x264-TEAM", "The Extended Cut", 2020)]
    [InlineData("The.RM.2020.MULTI.1080p.WEB.x264-TEAM", "The RM", 2020)]
    [InlineData("RM.2020.MULTI.1080p.WEB.x264-TEAM", "RM", 2020)]
    [InlineData("Tamil.2026.VOSTFR.1080p.WEBRip.x264-TEAM", "Tamil", 2026)]
    [InlineData("Anomie.2025.Tamil.VOSTFR.2026.1080p.WEBRip.x264-TEAM", "Anomie 2025 Tamil", 2026)]
    [InlineData("Film.1981.RM.1080p", "Film 1981 RM", null)]
    [InlineData("Film.1981.rm.MULTI.1080p.WEB.x264-TEAM", "Film 1981 rm", null)]
    [InlineData("Film.1991.Extended.Cut", "Film 1991 Extended Cut", null)]
    [InlineData("1917.2019.MULTI.1080p.WEB.x264-TEAM", "1917", 2019)]
    [InlineData("24.2020.MULTI.1080p.WEB.x264-TEAM", "24", 2020)]
    [InlineData("F1.2025.MULTI.1080p.WEB.x264-TEAM", "F1", 2025)]
    public void UncertainWordsAndTitleNumbersArePreserved(string raw, string title, int? year)
    {
        var parsed = new TitleParser().Parse(raw, UnifiedCategory.Film);
        Assert.Equal(title, parsed.TitleClean);
        Assert.Equal(year, parsed.Year);
    }

    [Fact]
    public void SportsFixtureExtractsOnlyAValidatedBroadcastDate()
    {
        const string raw = "NFL.W11.Minnesota.Vikings.at.San.Francisco.49ers.22.11.2026.FRENCH.1080p.HDTV.AAC.2.0.H264-Blap";
        var parsed = new TitleParser().Parse(raw, UnifiedCategory.Film);
        Assert.Equal("NFL W11 Minnesota Vikings at San Francisco 49ers", parsed.TitleClean);
        Assert.Equal("2026-11-22", parsed.AirDate);
        Assert.Equal(2026, parsed.Year);
        Assert.Equal("HDTV", parsed.Source);
        Assert.Equal("x264", parsed.Codec);
        Assert.Equal("Blap", parsed.ReleaseGroup);
        Assert.Equal(parsed.TitleClean, Assert.Single(SearchCandidateGenerator.Generate(new(raw, UnifiedCategory.Film))).Title);
        Assert.Contains("49ers", TitleNormalizer.NormalizeTitleStrict(parsed.TitleClean));
    }

    [Theory]
    [InlineData("Film.22.11.2026.FRENCH.1080p.HDTV.x264-TEAM")]
    [InlineData("NFL.W11.Team.at.49ers.31.02.2026.FRENCH.1080p.HDTV.x264-TEAM")]
    [InlineData("NFL.W11.Team.at.49ers.22.11.2026.FRENCH.1080p.BluRay.x264-TEAM")]
    public void UnprovenOrInvalidDatesRemainInTheTitle(string raw)
    {
        var parsed = new TitleParser().Parse(raw, UnifiedCategory.Film);
        Assert.Null(parsed.AirDate);
        Assert.Contains(raw.Contains("31.02") ? "31 02" : "22 11", parsed.TitleClean);
    }

    [Theory]
    [InlineData(UnifiedCategory.Serie)]
    [InlineData(UnifiedCategory.Animation)]
    [InlineData(UnifiedCategory.Anime)]
    [InlineData(UnifiedCategory.Emission)]
    [InlineData(UnifiedCategory.JeuWindows)]
    public void FilmEditionAndLanguageChangesStayScopedToFilms(UnifiedCategory category)
    {
        var parser = new TitleParser();
        Assert.Contains("RM", parser.Parse("Scanners.1981.RM.MULTI.VFF.1080p.BluRay.x265-TEAM", category).TitleClean);
        Assert.Contains("Tamil", parser.Parse("Anomie.2026.Tamil.VOSTFR.2026.1080p.WEBRip.x264-TEAM", category).TitleClean);
    }
    [Theory]
    [InlineData("Avengers.Endgame.Encore.2026.MULTi.VF2.1080p.WEB.H265-GL0P.mkv", UnifiedCategory.Film, "Avengers Endgame Encore", 2026)]
    [InlineData("Marshals.A.Yellowstone.Story.S02E07.MULTi.VFF.1080p.WEBrip.AC3.5.1.x265-JTBC.FRENCH", UnifiedCategory.Serie, "Marshals A Yellowstone Story", null)]
    [InlineData("Le.Monde.De.Nemo.2003.MULTI.VF2.1080p.BluRay.HDLight.AC3.5.1.x264-PopHD", UnifiedCategory.Animation, "Le Monde De Nemo", 2003)]
    [InlineData("Prescription.Musicale.S01E06.AD.VFQ.1080p.WEB.AAC.2.0.H264-MTLQC", UnifiedCategory.Emission, "Prescription Musicale", null)]
    public void AlreadyAssociatedLocalReleasesKeepTheirParsingAndProtectedQueryBehavior(string raw, UnifiedCategory category, string title, int? year)
    {
        var parsed = new TitleParser().Parse(raw, category);
        Assert.Equal(title, parsed.TitleClean);
        Assert.Equal(year, parsed.Year);
        var query = Assert.Single(SearchCandidateGenerator.Generate(new(raw, category)));
        Assert.Equal(title, query.Title);
        Assert.Equal(year, query.Year);
        Assert.Empty(SearchCandidateGenerator.Generate(new(raw, category, HasEstablishedExternalIdentity: true)));
    }

}
