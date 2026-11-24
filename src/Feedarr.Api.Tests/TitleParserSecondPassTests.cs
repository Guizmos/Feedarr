using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Tests;

public sealed class TitleParserSecondPassTests
{
    [Theory]
    [InlineData("DOC")]
    [InlineData("iNTERNAL")]
    [InlineData("DC")]
    [InlineData("LiMiTED")]
    [InlineData("IMAX")]
    [InlineData("AI.SUB")]
    public void DatedSingleWorkTagsNeedLanguageAndEstablishedTechnicalSuffix(string tag)
    {
        var parsed = new TitleParser().Parse($"Example.2020.{tag}.VOSTFR.1080p.BluRay.x264-TEAM", UnifiedCategory.Film);
        Assert.Equal("Example", parsed.TitleClean);
        Assert.Equal(2020, parsed.Year);
        var query = Assert.Single(SearchCandidateGenerator.Generate(new($"Example.2020.{tag}.VOSTFR.1080p.BluRay.x264-TEAM", UnifiedCategory.Film)));
        Assert.Equal("Example", query.Title); Assert.Equal(2020, query.Year);
    }

    [Theory]
    [InlineData("DOC")]
    [InlineData("iNTERNAL")]
    [InlineData("DC")]
    [InlineData("LiMiTED")]
    [InlineData("IMAX")]
    [InlineData("AI.SUB")]
    public void LegitimateWordsAndInsufficientEvidenceStayInTheTitle(string tag)
    {
        var parser = new TitleParser();
        var text = tag.Replace('.', ' ');
        Assert.Contains(text, parser.Parse($"The.{tag}.2020.VOSTFR.1080p.WEB.x264-TEAM", UnifiedCategory.Film).TitleClean);
        Assert.Contains(text, parser.Parse($"Example.2020.{tag}.1080p.WEB.x264-TEAM", UnifiedCategory.Film).TitleClean);
        Assert.Contains(text, parser.Parse($"Example.2020.{tag}.VOSTFR.1080p", UnifiedCategory.Film).TitleClean);
        Assert.Contains(text, parser.Parse($"Example.2020.{tag}.Unknown.Words.VOSTFR.1080p.WEB.x264-TEAM", UnifiedCategory.Film).TitleClean);
        Assert.Contains(text, parser.Parse($"Example.2020.{tag}.2026.VOSTFR.1080p.WEB.x264-TEAM", UnifiedCategory.Film).TitleClean);
    }

    [Theory]
    [InlineData("Le.Plus.Beau.Pays.Du.Monde.2013.2026.DOC.PACK.FRENCH.WEBRip.x264-RiFiFi")]
    [InlineData("Example.2020.DOC.PACK.FRENCH.1080p.WEB.x264-TEAM")]
    [InlineData("Example.2019.2020.DOC.FRENCH.1080p.WEB.x264-TEAM")]
    public void CompositeDocumentaryPacksAndDifferentYearsStayConservative(string raw)
    {
        var parsed = new TitleParser().Parse(raw, UnifiedCategory.Film);
        Assert.Contains("DOC", parsed.TitleClean);
        Assert.Null(parsed.Year);
    }

    [Theory]
    [InlineData("13.Jeux.de.mort.2006.LiMiTED.2006.VFF.720p.BluRay.AC3.5.1.x264-FHD", "13 Jeux de mort", 2006)]
    [InlineData("Heartbeasts.2022.Internal.2022.VFF.1080p.WEB.EAC3.2.0.H264-ukdhd", "Heartbeasts", 2022)]
    [InlineData("THX.1138.1971.MULTI.COMPLETE.BLURAY.iNTERNAL-VEiL", "THX 1138", 1971)]
    [InlineData("Les.Gardiens.de.la.Galaxie.Volume.3.2023.IMAX.MULTI.VF2.2160p.WEB.DV.HDR10PLUS.EAC3.7.1.H265-BATGirl", "Les Gardiens de la Galaxie Volume 3", 2023)]
    [InlineData("9.11.Cleared.For.Chaos.2019.Doc.FRENCH.1080p.HDTV.x265-RAVAGEURS", "9 11 Cleared For Chaos", 2019)]
    [InlineData("Sailor.Suit.and.Machine.Gun.1981.DC.VOSTFR.1080p.BluRay.FLAC.1.0.x264-ZoroSenpai", "Sailor Suit and Machine Gun", 1981)]
    [InlineData("Street.Fighter.2026.AI.SUB.VOSTFR.1080p.WEB.H264.AAC-Cortex91", "Street Fighter", 2026)]
    public void RealReleaseExamplesPreserveTitleNumbers(string raw, string title, int year)
    {
        var parsed = new TitleParser().Parse(raw, UnifiedCategory.Film);
        Assert.Equal(title, parsed.TitleClean); Assert.Equal(year, parsed.Year);
    }

    [Fact]
    public void GermanLanguageBridgesResolutionAndKnownSourceWithoutGlobalStopword()
    {
        var parser = new TitleParser();
        var parsed = parser.Parse("Les.Bronzés.1978.2160p.GER.UHD.Blu-ray.DoVi.HDR10.HEVC.DTS-HD.MA.5.1-NoTag", UnifiedCategory.Film);
        Assert.Equal("Les Bronzés", parsed.TitleClean); Assert.Equal(1978, parsed.Year);
        Assert.Equal("2160p", parsed.Resolution); Assert.Equal("BluRay", parsed.Source);
        Assert.Contains("GER", parser.Parse("The.GER.2020.VOSTFR.1080p.WEB.x264-TEAM", UnifiedCategory.Film).TitleClean);
        Assert.Contains("GER", parser.Parse("Example.2020.GER.1080p.WEB.x264-TEAM", UnifiedCategory.Film).TitleClean);
        Assert.Contains("GER", parser.Parse("Example.2020.2160p.GER.Unknown.Story.BluRay.x264-TEAM", UnifiedCategory.Film).TitleClean);
    }

    [Fact]
    public void AiSubRequiresAnExplicitSubtitleDeclaration()
    {
        var parser = new TitleParser();
        Assert.Contains("AI SUB", parser.Parse("Film.2026.AI.SUB.FRENCH.1080p.WEB.x264-TEAM", UnifiedCategory.Film).TitleClean);
        Assert.Contains("AI Story", parser.Parse("The.AI.Story.2026.VOSTFR.1080p.WEB.x264-TEAM", UnifiedCategory.Film).TitleClean);
    }

    [Fact]
    public void NbaIsoDateIsExtractedWithoutLosingTheTeams()
    {
        const string raw = "NBA-2026-11-22.BOS.VS.GSW.2026.VO.720p.WEB-DL.AAC.2.0.H264-CLTX";
        var parsed = new TitleParser().Parse(raw, UnifiedCategory.Film);
        Assert.Equal("NBA BOS VS GSW", parsed.TitleClean);
        Assert.Equal("2026-11-22", parsed.AirDate); Assert.Equal(2026, parsed.Year);
        Assert.Equal("720p", parsed.Resolution); Assert.Equal("CLTX", parsed.ReleaseGroup);
        Assert.Equal("NBA BOS VS GSW", Assert.Single(SearchCandidateGenerator.Generate(new(raw, UnifiedCategory.Film))).Title);
    }

    [Theory]
    [InlineData("NBA-2026-02-31.BOS.VS.GSW.2026.VO.720p.WEB-DL.H264-TEAM")]
    [InlineData("NBA-2026-11-22.BOS.VS.GSW.2025.VO.720p.WEB-DL.H264-TEAM")]
    [InlineData("NBA-2026-11-22.The.Real.Story.2026.VO.720p.WEB-DL.H264-TEAM")]
    [InlineData("NBA-2026-11-22.BOS.VS.GSW.2026.VO.720p.BluRay.H264-TEAM")]
    public void UncertainNbaDateOrFixtureRemainsUntouched(string raw)
    {
        var parsed = new TitleParser().Parse(raw, UnifiedCategory.Film);
        Assert.Null(parsed.AirDate);
        Assert.Contains(raw.Contains("02-31") ? "2026-02-31" : "2026-11-22", parsed.TitleClean);
    }

    [Theory]
    [InlineData(UnifiedCategory.JeuWindows)]
    [InlineData(UnifiedCategory.Audio)]
    [InlineData(UnifiedCategory.Book)]
    [InlineData(UnifiedCategory.Comic)]
    [InlineData(UnifiedCategory.Serie)]
    [InlineData(UnifiedCategory.Animation)]
    [InlineData(UnifiedCategory.Anime)]
    [InlineData(UnifiedCategory.Emission)]
    public void OtherCategoriesKeepEditionWordsAndNumbers(UnifiedCategory category)
    {
        var parser = new TitleParser();
        foreach (var tag in new[] { "DOC", "iNTERNAL", "DC", "LIMITED", "IMAX", "AI.SUB" })
        {
            var parsed = parser.Parse($"F1.24.1917.2026.{tag}.VOSTFR.1080p.WEB.x264-TEAM", category);
            // Executed reference parser: games already treat 2026 as distribution metadata.
            if (category == UnifiedCategory.JeuWindows)
            { Assert.Equal("F1 24 1917", parsed.TitleClean); Assert.Equal(2026, parsed.Year); }
            else Assert.Contains(tag.Replace('.', ' '), parsed.TitleClean);
        }
        Assert.Contains("GER", parser.Parse("Title.2020.2160p.GER.UHD.BluRay.x264-TEAM", category).TitleClean);
    }

    [Theory]
    [InlineData(UnifiedCategory.JeuWindows)]
    [InlineData(UnifiedCategory.Audio)]
    [InlineData(UnifiedCategory.Book)]
    public void NbaRulesDoNotDateOtherMedia(UnifiedCategory category)
    {
        var parsed = new TitleParser().Parse("NBA-2026-11-22.BOS.VS.GSW.2026.VO.720p.WEB-DL.H264-TEAM", category);
        Assert.Null(parsed.AirDate);
    }
}
