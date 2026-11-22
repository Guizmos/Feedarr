using Feedarr.Api.Models;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Tests;

public sealed class TitleParserFilmEditionTests
{
    // REMASTERED before a sole year is retained: case does not prove an edition.
    [Theory]
    [InlineData("La.Fille.du.puisatier.REMASTERED.1940.VOF.1080p.NF.WEB-DL.H.264.AAC.2.0-Caren.mkv", "La Fille du puisatier REMASTERED", 1940)]
    [InlineData("Gunfight.at.the.O.K.Corral.REMASTERED.1957.MULTi.VFi.1080p.BluRay.REMUX.AVC.DTS-HD.MA.5.1-Caren.mkv", "Gunfight at the O K Corral REMASTERED", 1957)]
    [InlineData("Spawn.1997.Theatrical.Cut.MULTi.VFF.2160p.BluRay.DV.HDR.DTS.HD.MA.5.1.x265-XANDER.mkv", "Spawn", 1997)]
    [InlineData("The.Town.2010.THEATRICAL.MULTi.VFF.1080p.BluRay.REMUX.DTS.HD.MA.5.1.AVC-Freek911", "The Town", 2010)]
    [InlineData("Avengers.Endgame.2019.Encore.Edition.2026.AD.MULTi.VF2.1080p.WEB.DDP5.1.H264-SUPPLY", "Avengers Endgame", 2019)]
    [InlineData("Casablanca.1942.70th.Anniversary.VOSTFR.1080p.BluRay.FLAC.1.0.x265-SARTRE", "Casablanca", 1942)]
    [InlineData("Film.Encore.Edition.2026.MULTI.VFF.1080p.WEB.H264-TEAM", "Film", null)]
    [InlineData("Film.70th.Anniversary.2026.MULTI.VFF.1080p.BluRay.x264-TEAM", "Film", null)]
    [InlineData("Blade.Runner.2049.Encore.Edition.2026.MULTI.VFF.1080p.WEB.H264-TEAM", "Blade Runner 2049", null)]
    [InlineData("Film.2019.Encore.Edition.(2026).MULTI.VFF.1080p.WEB.H264-TEAM", "Film", 2019)]
    [InlineData("Film.2019.Encore.Edition.2018.MULTI.VFF.1080p.WEB.H264-TEAM", "Film 2019", null)]
    public void Parse_FilmEditionSeparatesWorkAndDistributionDates(string raw, string title, int? year)
    {
        var parsed = new TitleParser().Parse(raw, UnifiedCategory.Film);

        Assert.Equal(title, parsed.TitleClean);
        Assert.Equal(year, parsed.Year);
        Assert.Null(parsed.Season);
        Assert.Null(parsed.Episode);
    }

    [Theory]
    [InlineData("The.Theatrical.Story.2020.MULTI.1080p.WEB.H264-TEAM", "The Theatrical Story", 2020)]
    [InlineData("The.Theatrical.2020.MULTI.1080p.WEB.H264-TEAM", "The Theatrical", 2020)]
    [InlineData("The.REMASTERED.Story.2020.MULTI.1080p.WEB.H264-TEAM", "The REMASTERED Story", 2020)]
    [InlineData("The.Anniversary.Edition.2020.MULTI.1080p.WEB.H264-TEAM", "The Anniversary Edition", 2020)]
    [InlineData("The.Remastered.2020.MULTI.1080p.WEB.H264-TEAM", "The Remastered", 2020)]
    [InlineData("The.REMASTERED.2020.MULTI.1080p.WEB.H264-TEAM", "The REMASTERED", 2020)]
    [InlineData("The.Anniversary.2020.MULTI.1080p.WEB.H264-TEAM", "The Anniversary", 2020)]
    [InlineData("The.Edition.2020.MULTI.1080p.WEB.H264-TEAM", "The Edition", 2020)]
    [InlineData("The.Theatrical.Cut.2020.MULTI.1080p.WEB.H264-TEAM", "The Theatrical Cut", 2020)]
    [InlineData("Remastered.2020.MULTI.1080p.WEB.H264-TEAM", "Remastered", 2020)]
    [InlineData("Film.REMASTERED.1940", "Film REMASTERED 1940", null)]
    [InlineData("Film.1997.Theatrical.Cut.1080p", "Film 1997 Theatrical Cut", null)]
    [InlineData("Film.2019.Encore.Edition.2026", "Film 2019 Encore Edition 2026", null)]
    [InlineData("Film.2019.Unknown.Edition.2026.MULTI.1080p.WEB.H264-TEAM", "Film 2019 Unknown Edition", 2026)]
    public void Parse_FilmEditionPreservesTitleWordsAndUnprovenEditions(string raw, string title, int? year)
    {
        var parsed = new TitleParser().Parse(raw, UnifiedCategory.Film);

        Assert.Equal(title, parsed.TitleClean);
        Assert.Equal(year, parsed.Year);
    }

    [Theory]
    [InlineData(UnifiedCategory.Serie)]
    [InlineData(UnifiedCategory.Animation)]
    [InlineData(UnifiedCategory.Anime)]
    [InlineData(UnifiedCategory.Emission)]
    public void Parse_FilmEditionRulesDoNotApplyToOtherCategories(UnifiedCategory category)
    {
        var parsed = new TitleParser().Parse("Spawn.1997.Theatrical.Cut.MULTI.1080p.BluRay.x264-TEAM", category);

        Assert.Equal("Spawn 1997 Theatrical Cut", parsed.TitleClean);
        Assert.Null(parsed.Year);
    }
}
