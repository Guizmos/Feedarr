using Feedarr.Api.Models;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Tests;

public sealed class TitleParserSuffixSequenceTests
{
    // Public work titles; private release signatures replaced with structural equivalents.
    // These are lexical parser references, never assertions of provider identity.
    [Theory]
    [InlineData("Miyazaki.Ponyo.Sur.La.Falaise.2008.FRENCH.BRRip.x264.AC3-TEAM.ONE", "Miyazaki Ponyo Sur La Falaise", 2008, "TEAM.ONE", "BluRay", "x264")]
    [InlineData("Casino.1995.VFF.SD.BDRip.AAC.2.0.x264-TEAM", "Casino", 1995, "TEAM", "BluRay", "x264")]
    [InlineData("Ce.Que.Mes.Yeux.Ont.Vu.2007.VFF.SD.DVDRip.AAC.2.0.x264-TEAM", "Ce Que Mes Yeux Ont Vu", 2007, "TEAM", "DVDRip", "x264")]
    [InlineData("Yu-Gi-Oh.Reunis.Au.Dela.Du.temps.2010.MULTI.1080p.BRRip.x265.AAC-TEAM.ONE.FRENCH", "Yu-Gi-Oh Reunis Au Dela Du temps", 2010, "TEAM.ONE.FRENCH", "BluRay", "x265")]
    [InlineData("Maboroshi.2023.MULTi.AD.1080p.WEB.DDP5.1.X264.TEAM.FRENCH", "Maboroshi", 2023, null, null, "x264")]
    [InlineData("Film.2020.VFF.AD.DSNP.1080p.WEB.HDR10Plus.EAC3.5.1.H265-TEAM", "Film", 2020, "TEAM", null, "x265")]
    [InlineData("Film.2020.VFF.SD.DVDRip.PCM.1.0.MPEG2-TEAM", "Film", 2020, "TEAM", "DVDRip", "MPEG2")]
    [InlineData("Film.2020.1080p.AMZN.WEB-DL.H.264.UnknownDistribution-TEAM", "Film", 2020, null, "WEB-DL", "x264")]
    [InlineData("The.DTS.Story.2020.1080p.WEB-DL.x264-TEAM", "The DTS Story", 2020, "TEAM", "WEB-DL", "x264")]
    [InlineData("The.AD.Story.2020.1080p.WEB-DL.x264-TEAM", "The AD Story", 2020, "TEAM", "WEB-DL", "x264")]
    [InlineData("The.PCM.Story.2020.1080p.WEB-DL.x264-TEAM", "The PCM Story", 2020, "TEAM", "WEB-DL", "x264")]
    [InlineData("The.SD.Story.2020.1080p.WEB-DL.x264-TEAM", "The SD Story", 2020, "TEAM", "WEB-DL", "x264")]
    [InlineData("The.AMZN.Story.2020.1080p.WEB-DL.x264-TEAM", "The AMZN Story", 2020, "TEAM", "WEB-DL", "x264")]
    [InlineData("The.Ad.2020.1080p.WEB-DL.x264-TEAM", "The Ad", 2020, "TEAM", "WEB-DL", "x264")]
    [InlineData("The.Last.King.Of.Scotland.2006.PROPER.MULTi.VFF.1080p.BluRay.x264-TEAM", "The Last King Of Scotland", 2006, "TEAM", "BluRay", "x264")]
    [InlineData("Twilight.Chapitre.3.Hesitation.2010.MULTI.VF2.2160p.HYBRID.BluRay.DV.HDR10.AAC.5.1.x265-TEAM", "Twilight Chapitre 3 Hesitation", 2010, "TEAM", "BluRay", "x265")]
    [InlineData("Film.2025.VFF.BDRip.AAC2.0.x264-TEAM", "Film", 2025, "TEAM", "BluRay", "x264")]
    [InlineData("Film.2025.1080p.PAPR.WEB-DL.H.264-TEAM", "Film", 2025, "TEAM", "WEB-DL", "x264")]
    [InlineData("Film.2025.1080p.DISNEY.WEBRip.H.264-TEAM", "Film", 2025, "TEAM", "WEBRip", "x264")]
    [InlineData("Film.2025.1080p.WEB.HDR10P.H265-TEAM", "Film", 2025, "TEAM", null, "x265")]
    [InlineData("Lilo.Et.Stitch.2002.FRENCH.DVDRiP.X264.AC3.UnknownTranslation", "Lilo Et Stitch", 2002, null, "DVDRip", "x264")]
    [InlineData("Lupin.Iii.Le.Tombeau.De.Daisuke.Jigen.MULTi.1080p.BLURAY.X265.UnknownTranslation", "Lupin Iii Le Tombeau De Daisuke Jigen", null, null, "BluRay", "x265")]
    [InlineData("Les.Aventures.De.Tintin.Les.Cigares.Du.Pharaon.1991.MULTi.1080p.BLURAY.X264-TEAM.FRENCH", "Les Aventures De Tintin Les Cigares Du Pharaon", 1991, "TEAM.FRENCH", "BluRay", "x264")]
    [InlineData("Les.Octonauts.Et.La.Grande.Barriere.De.Corail.1080p.MULTi.WEB.H264.UnknownTranslation", "Les Octonauts Et La Grande Barriere De Corail", null, null, null, "x264")]
    [InlineData("Lilo.Stitch.2002.MULTi.VF2.1080p.BLURAY.X265.10BiT.OPUS.51.UnknownTranslation", "Lilo Stitch", 2002, null, "BluRay", "x265")]
    [InlineData("Lodyssee.De.Pi.2012.MULTi.VFF.VO.VFQ.1080p.BLURAY.X264.AC3.UnknownTranslation", "Lodyssee De Pi", 2012, null, "BluRay", "x264")]
    [InlineData("Lucky.Luke.La.Ballade.Des.Dalton.1978.FRENCH.1080p.WEB.H264.UnknownSignature", "Lucky Luke La Ballade Des Dalton", 1978, null, null, "x264")]
    [InlineData("Mononoke.The.Movie.Chapter.II.The.Ashes.of.Rage.2025.MULTi.AD.1080p.NF.WEB.DV.HDR.H265-TEAM.FRENCH", "Mononoke The Movie Chapter II The Ashes of Rage", 2025, "TEAM.FRENCH", null, "x265")]
    [InlineData("Les.Contes.du.pommier.2025.FRENCH.AD.CNLP.1080p.WEB.x264-TEAM", "Les Contes du pommier", 2025, "TEAM", null, "x264")]
    [InlineData("La.Maldiction.De.Provins.2019.VFF.1080p.HDTV.AAC.2.0.H264-TEAM.FRENCH", "La Maldiction De Provins", 2019, "TEAM.FRENCH", "HDTV", "x264")]
    [InlineData("La.Belle.et.l.Empereur.1959.VFF.SD.DVDRip.AAC.2.0.x264-TEAM", "La Belle et l Empereur", 1959, "TEAM", "DVDRip", "x264")]
    [InlineData("Song.For.A.Raggy.Boy.2003.VOSTFR.SD.MP3.2.0.XviD-TEAM", "Song For A Raggy Boy", 2003, "TEAM", null, null)]
    [InlineData("The.Ad.1080p.WEB-DL.x264-TEAM", "The Ad", null, "TEAM", "WEB-DL", "x264")]
    [InlineData("The.SD.1080p", "The SD", null, null, null, null)]
    [InlineData("The.PCM.1080p", "The PCM", null, null, null, null)]
    [InlineData("The.AD.1080p.WEB-DL.x264-TEAM", "The AD", null, "TEAM", "WEB-DL", "x264")]
    [InlineData("The.IT.1080p.WEB-DL.x264-TEAM", "The IT", null, "TEAM", "WEB-DL", "x264")]
    [InlineData("Film.2020.576p", "Film 2020", null, null, null, null)]
    public void Parse_VideoSequencePreservesTheTitle(string raw, string title, int? year, string? group, string? source, string? codec)
    {
        var parsed = new TitleParser().Parse(raw, UnifiedCategory.Film);
        Assert.Equal(title, parsed.TitleClean);
        Assert.Equal(year, parsed.Year);
        Assert.Equal(group, parsed.ReleaseGroup);
        Assert.Equal(source, parsed.Source);
        Assert.Equal(codec, parsed.Codec);
        Assert.Equal("movie", parsed.MediaType);
        Assert.Null(parsed.Season);
        Assert.Null(parsed.Episode);
    }

    [Theory]
    [InlineData("Bodycam.v0.8.12.19346.WIN.X64.MULTI.PORTABLE.PREACTIVED-TEAM", "Bodycam")]
    [InlineData("The.Dark.Pictures.Anthology.Directive.8020.V26268280.WIN.X64.MULTI.PORTABLE.PREACTIVED-TEAM", "The Dark Pictures Anthology Directive 8020")]
    [InlineData("Cyberpunk.2077.v2.5.WIN.UnknownDistribution-TEAM", "Cyberpunk 2077")]
    [InlineData("F1.2026.Update.v1.2.WIN.PREACTIVED-TEAM", "F1 2026")]
    [InlineData("Portal.2.v1.2.WIN.UnknownDistribution-TEAM", "Portal 2")]
    [InlineData("The.Update.Story.v1.2.WIN.PREACTIVED-TEAM", "The Update Story")]
    [InlineData("The.Linux.Tycoon.v1.2.WIN.PREACTIVED-TEAM", "The Linux Tycoon")]
    [InlineData("The.Origin.PC-GOG", "The Origin")]
    [InlineData("Game.Deluxe.Edition.The.Return.v1.2.WIN.PREACTIVED-TEAM", "Game Deluxe Edition The Return")]
    [InlineData("Game.Title.(Build.26227185.+.2.DLCs)-[CODEX.Repack]", "Game Title")]
    [InlineData("Game.2.(v1.0.20261103.004.+.DLC)-[CODEX.Repack]", "Game 2")]
    [InlineData("Game.2.[CODEX.HV.Repack]", "Game 2")]
    [InlineData("Croc.2.Kingdom.of.the.Gobbos-[CODEX.Repack]", "Croc 2 Kingdom of the Gobbos")]
    [InlineData("Game.Keeper.Somebody.Messed.Up.My.Shop!-[CODEX.Repack]", "Game Keeper Somebody Messed Up My Shop!")]
    [InlineData("Game.Title.(+.2.DLCs)-[CODEX.Repack]", "Game Title")]
    [InlineData("Game.Title.(Bonus.OST).v1.0-GOG", "Game Title")]
    public void Parse_GameSequencePreservesNamesAndNumbers(string raw, string title)
    {
        var parsed = new TitleParser().Parse(raw, UnifiedCategory.JeuWindows);
        Assert.Equal(title, parsed.TitleClean);
        Assert.Null(parsed.Year);
        Assert.Equal("game", parsed.MediaType);
        Assert.Null(parsed.Season);
        Assert.Null(parsed.Episode);
    }

    [Fact]
    public void Parse_CompoundGroupAfterCodec_DoesNotUseLastHyphen()
    {
        var parsed = new TitleParser().Parse("Show.S01E02.1080p.WEB-DL.x264-TEAM-ONE.CR", UnifiedCategory.Serie);
        Assert.Equal("Show", parsed.TitleClean);
        Assert.Equal("TEAM-ONE.CR", parsed.ReleaseGroup);
        Assert.Equal(1, parsed.Season);
        Assert.Equal(2, parsed.Episode);
        Assert.Equal("WEB-DL", parsed.Source);
        Assert.Equal("1080p", parsed.Resolution);
    }

    [Fact]
    public void Parse_EmissionAndAnimationContractsRemainUnchanged()
    {
        var parser = new TitleParser();
        var emission = parser.Parse("Show.S21E15.02.11.2026.VFF.1080p.WEB.H264-TEAM", UnifiedCategory.Emission);
        Assert.Equal("2026-11-02", emission.AirDate);
        Assert.Null(emission.Season);
        Assert.Null(emission.Episode);
        var animation = parser.Parse("Show.S01E03.1080p.WEB.H264-TEAM", UnifiedCategory.Animation);
        Assert.Equal("movie", animation.MediaType);
        Assert.Null(animation.Season);
        Assert.Null(animation.Episode);
        Assert.Contains("S01E03", animation.TitleClean);
    }
}
