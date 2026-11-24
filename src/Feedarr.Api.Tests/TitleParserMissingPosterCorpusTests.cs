using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Tests;

public sealed class TitleParserMissingPosterCorpusTests
{
    [Theory]
    [InlineData("Darkman.1990.RESMASTERED.CUSTOM.MULTi.VFi.1080p.HDLight.AC3.2.0.AC3.5.1.H264-LiHDL.FRENCH", "Darkman", 1990)]
    [InlineData("Superbad.2007.UNRATED.MULTi.1080p.BluRay.x264-ZiT", "Superbad", 2007)]
    [InlineData("Innocence.2020.KOREAN.VOSTFR.1080p.WEB.AAC.HEVC-NoTag", "Innocence", 2020)]
    [InlineData("Redux.Redux.2025.Fansub.Vostfr.1080P.Webrip.x265.10bit-Maxadonf", "Redux Redux", 2025)]
    [InlineData("Eye.For.An.Eye.2025.FANSUB.VOSTFR.1080p.WEB.AC3.5.1.H264-Slay3R", "Eye For An Eye", 2025)]
    public void DatedFilmMetadataRequiresASubtitleOrLanguageAndTechnicalChain(string raw, string title, int year)
    {
        var parsed = new TitleParser().Parse(raw, UnifiedCategory.Film);
        Assert.Equal(title, parsed.TitleClean);
        Assert.Equal(year, parsed.Year);
    }

    [Theory]
    [InlineData("The.Moment.You.Fall.in.Love.2016.VOSTFR.FANSUB.1080p.BluRay.DTS.HD.MA.5.1.x264-Neutrinome", "The Moment You Fall in Love")]
    [InlineData("I.ve.Always.Liked.You.2016.VOSTFR.FANSUB.1080p.BluRay.10bit.OPUS.2.0.AV1-Trix", "I ve Always Liked You")]
    [InlineData("I.ve.Always.Liked.You.2016.VOSTFR.FANSUB.1080p.BluRay.DTS.HD.MA.5.1.x264-Neutrinome", "I ve Always Liked You")]
    public void AnimationSubtitleMetadataDoesNotImplyEpisodeRouting(string raw, string title)
    {
        var parsed = new TitleParser().Parse(raw, UnifiedCategory.Animation);
        Assert.Equal(title, parsed.TitleClean);
        Assert.Equal(2016, parsed.Year);
        Assert.Null(parsed.Season);
        Assert.Equal("movie", parsed.MediaType);
    }

    [Theory]
    [InlineData("Theatre.Normal.de.Jane.Anderson", "Theatre Normal de Jane Anderson")]
    [InlineData("Mon.jour.de.chance", "Mon jour de chance")]
    [InlineData("Diane.Segard.Parades.Live", "Diane Segard Parades Live")]
    public void FullFranceTvTheatreExportSuppliesContextForTheBareResolution(string prefix, string title)
    {
        var parsed = new TitleParser().Parse(prefix + ".FranceTV.2026.VFF.1080.WEB.AVC.AAC.NoTag.FRENCH", UnifiedCategory.Spectacle);
        Assert.Equal(title, parsed.TitleClean);
        Assert.Equal(2026, parsed.Year);
        Assert.Equal("1080p", parsed.Resolution);
        Assert.Equal("x264", parsed.Codec);
    }

    [Theory]
    [InlineData("Sniper.Elite.5.Complete.Edition.v2.41.incl.DLCs.MULTi12.Repack-FitGirl", "Sniper Elite 5 Complete Edition")]
    [InlineData("The.Legend.of.Zelda.Links.Awakening.v1.1.0.RECOMPiLED.MULTi11-ElAmigos", "The Legend of Zelda Links Awakening")]
    [InlineData("Super.Mario.Odyssey.v1.4.1.RECOMPiLED.MULTi12-ElAmigos", "Super Mario Odyssey")]
    [InlineData("Game.Deluxe.Edition.v2.41.incl.DLCs.MULTi12.Repack-RUNE", "Game Deluxe Edition")]
    [InlineData("Game.Premium.Edition.v2.41.incl.DLCs.MULTi12.Repack-FitGirl", "Game Premium Edition")]
    [InlineData("Game.Complete.Collection.v2.41.incl.DLCs.MULTi12.Repack-FitGirl", "Game Complete Collection")]
    [InlineData("Game.DLC.Quest.Bundle.v2.41.incl.DLCs.MULTi12.Repack-FitGirl", "Game DLC Quest Bundle")]
    public void KnownVersionContinuationsRetainIdentityWordsBeforeTheVersion(string raw, string title)
    {
        var parsed = new TitleParser().Parse(raw, UnifiedCategory.JeuWindows);
        Assert.Equal(title, parsed.TitleClean);
        Assert.Null(parsed.Year);
    }

    [Theory]
    [InlineData("The.UNRATED.2007.MULTi.1080p.BluRay.x264-TEAM", "The UNRATED", 2007)]
    [InlineData("The.RESMASTERED.1990.MULTi.1080p.WEB.x264-TEAM", "The RESMASTERED", 1990)]
    [InlineData("KOREAN.2020.VOSTFR.1080p.WEB.x264-TEAM", "KOREAN", 2020)]
    [InlineData("Fansub.2025.VOSTFR.1080p.WEB.x264-TEAM", "Fansub", 2025)]
    [InlineData("Film.2007.UNRATED.1080p", "Film 2007 UNRATED", null)]
    [InlineData("Film.2020.KOREAN.1080p.WEB.x264-TEAM", "Film 2020 KOREAN", null)]
    [InlineData("Film.2020.Fansub.1080p.WEB.x264-TEAM", "Film 2020 Fansub", null)]
    [InlineData("Film.2020.KOREAN.Story.VOSTFR.1080p.WEB.x264-TEAM", "Film 2020 KOREAN Story", null)]
    [InlineData("1917.2019.MULTi.1080p.WEB.x264-TEAM", "1917", 2019)]
    [InlineData("Blade.Runner.2049.2017.MULTi.1080p.WEB.x264-TEAM", "Blade Runner 2049", 2017)]
    public void AmbiguousWordsAndLegitimateTitleNumbersRemain(string raw, string title, int? year)
    {
        var parsed = new TitleParser().Parse(raw, UnifiedCategory.Film);
        Assert.Equal(title, parsed.TitleClean);
        Assert.Equal(year, parsed.Year);
    }

    [Theory]
    [InlineData("Game.v1.1.0.RECOMPILED.Story.MULTi11-ElAmigos", "RECOMPILED Story")]
    [InlineData("Game.v1.1.0.RECOMPILED.MULTi11-UnknownName", "v1 1 0 RECOMPILED")]
    [InlineData("Game.v1.1.0.incl.DLC.Quest.MULTi11-FitGirl", "DLC Quest")]
    [InlineData("Game.v1.1.0.incl.DLCs.MULTi12.Repack-FitGirl.Story", "v1 1 0 incl")]
    public void UnknownGameVersionContinuationsAreNotDiscarded(string raw, string retained)
        => Assert.Contains(retained, new TitleParser().Parse(raw, UnifiedCategory.JeuWindows).TitleClean);

    [Theory]
    [InlineData(UnifiedCategory.Film)]
    [InlineData(UnifiedCategory.Serie)]
    [InlineData(UnifiedCategory.Animation)]
    [InlineData(UnifiedCategory.JeuWindows)]
    public void FranceTvExportRepairIsScopedToTheatre(UnifiedCategory category)
        => Assert.Contains("FranceTV", new TitleParser().Parse(
            "Title.FranceTV.2026.VFF.1080.WEB.AVC.AAC.NoTag.FRENCH", category).TitleClean);

    [Theory]
    [InlineData("The.FranceTV.Story.2026.VFF.1080p.WEB.AVC.AAC-TEAM", "The FranceTV Story")]
    [InlineData("Title.FranceTV.2026", "Title FranceTV 2026")]
    public void FranceTvInsideAnOfficialTitleOrWithoutTheExportChainRemains(string raw, string title)
        => Assert.Equal(title, new TitleParser().Parse(raw, UnifiedCategory.Spectacle).TitleClean);

    [Theory]
    [InlineData("Metal.Gear.Solid.V.Complete-voices38", "Metal Gear Solid V Complete")]
    [InlineData("Game.Deluxe.Edition-voices38", "Game Deluxe Edition")]
    [InlineData("Game.Premium.Edition-voices38", "Game Premium Edition")]
    [InlineData("Game.DLC.Quest.Bundle-voices38", "Game DLC Quest Bundle")]
    public void IsolatedVoicesSignatureKeepsTheExistingAlternativeAndEditions(string raw, string title)
    {
        Assert.Equal(title + "-voices38", new TitleParser().Parse(raw, UnifiedCategory.JeuWindows).TitleClean);
        var queries = SearchCandidateGenerator.Generate(new(raw, UnifiedCategory.JeuWindows));
        Assert.Equal(title, queries[1].Title);
        Assert.Equal(SearchCandidateOrigin.IsolatedDistributionSuffix, queries[1].Origin);
    }

    [Theory]
    [InlineData("Cowboy.Bebop.INTEGRALE.S01.FRENCH.1080p.Bluray.10bits.AC3.5.1.x264", "Cowboy Bebop INTEGRALE")]
    [InlineData("Samurai.Champloo.INTEGRALE.S01.FRENCH.1080p.AAC.2.0.x264", "Samurai Champloo INTEGRALE")]
    [InlineData("Maths.Intégrale.S01E02.1080p", "Maths Intégrale")]
    [InlineData("Mission.COMPLETE.S01.1080p.WEB.H264-TEAM", "Mission COMPLETE")]
    public void SeasonAndPackWordsAloneDoNotProveAnOfficialTitleBoundary(string raw, string title)
        => Assert.Equal(title, new TitleParser().Parse(raw, UnifiedCategory.Serie).TitleClean);

    [Fact]
    public void AlbatorRoutingAndAmbiguousYearsStayUnchanged()
    {
        var parsed = new TitleParser().Parse("Albator84.1982.S01.COMPLETE.VFF.DVDRiP.AVC-NoTag.FRENCH", UnifiedCategory.Animation);
        Assert.Equal("Albator84 1982 S01", parsed.TitleClean);
        Assert.Equal("movie", parsed.MediaType);
        Assert.Null(parsed.Season);
        var queries = SearchCandidateGenerator.Generate(new("The.Lowdown.2025.S02E05.MULTi.VFF.1080p.WEBrip.EAC3.5.1.x265-JTBC.FRENCH", UnifiedCategory.Serie));
        Assert.Equal("The Lowdown 2025", queries[0].Title);
        Assert.Equal("The Lowdown", queries[1].Title);
        Assert.Equal(CandidateYearEvidence.Hypothesis, queries[1].YearEvidence);
        Assert.Equal("F1 2026", new TitleParser().Parse("F1.2026.PC-GOG", UnifiedCategory.JeuWindows).TitleClean);
    }

    [Fact]
    public void AlbatorMovieFamilyAndSeasonOnlyRemainConflictingRoutingEvidence()
    {
        var release = new ReleaseForPoster {
            Id = 25038, Title = "Albator84.1982.S01.COMPLETE.VFF.DVDRiP.AVC-NoTag.FRENCH",
            UnifiedCategory = "Animation", CategoryId = 102101, StdCategoryId = 2000,
            SpecCategoryId = 102101, CategoryIds = "2000,102101", MediaType = "movie"
        };
        Assert.Null(PosterFetchService.ResolveVideoType(release, UnifiedCategory.Animation));
        Assert.Equal("Animation", release.UnifiedCategory);
        Assert.Equal("2000,102101", release.CategoryIds);
    }

    [Theory]
    [InlineData("NBA-2026.11.23.UTA.VS.LAL.2026.FRENCH.1080p.WEB.DL.x264-TEAM")]
    [InlineData("NFL.W11.Cincinnati.Bengals.at.Washington.Commanders.23.11.2026.FRENCH.1080p.HDTV.x264-TEAM")]
    public void SportsStoredAsFilmsAreNotReclassifiedByParsing(string raw)
    {
        Assert.Equal("movie", new TitleParser().Parse(raw, UnifiedCategory.Film).MediaType);
        Assert.Equal("movie", PosterFetchService.ResolveVideoType(new ReleaseForPoster { Title = raw }, UnifiedCategory.Film));
    }

    [Theory]
    [InlineData("Eye.For.An.Eye.2025.FANSUB.VOSTFR.1080p.WEB.AC3.5.1.H264-Slay3R", UnifiedCategory.Film)]
    [InlineData("I.ve.Always.Liked.You.2016.VOSTFR.FANSUB.1080p.BluRay.10bit.OPUS.2.0.AV1-Trix", UnifiedCategory.Animation)]
    public void EstablishedAssociationsStillGenerateNoQueries(string raw, UnifiedCategory category)
    {
        Assert.Empty(SearchCandidateGenerator.Generate(new(raw, category, HasEstablishedExternalIdentity: true)));
        Assert.Empty(SearchCandidateGenerator.Generate(new(raw, category, HasManualCorrection: true)));
    }
}
