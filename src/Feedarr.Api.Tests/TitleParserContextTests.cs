using Feedarr.Api.Models;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Tests;

public sealed class TitleParserContextTests
{
    [Theory]
    [InlineData("The.French.Dispatch", UnifiedCategory.Film, "The French Dispatch", null, null, null, "movie")]
    [InlineData("The.French.Dispatch.2021.FRENCH.1080p.BluRay.x264-GRP", UnifiedCategory.Film, "The French Dispatch", 2021, null, null, "movie")]
    [InlineData("The.English.Patient.1996.MULTI.VFF.1080p", UnifiedCategory.Film, "The English Patient", 1996, null, null, "movie")]
    [InlineData("The.Collection.2012.1080p.WEB-DL-GRP", UnifiedCategory.Film, "The Collection", 2012, null, null, "movie")]
    [InlineData("Johnny.English.2011.1080p", UnifiedCategory.Film, "Johnny English", 2011, null, null, "movie")]
    [InlineData("Johnny.English.1080p", UnifiedCategory.Film, "Johnny English", null, null, null, "movie")]
    [InlineData("Le.Français.2015.MULTI.1080p", UnifiedCategory.Film, "Le Français", 2015, null, null, "movie")]
    [InlineData("The.Spanish.Prisoner.1997.1080p", UnifiedCategory.Film, "The Spanish Prisoner", 1997, null, null, "movie")]
    [InlineData("The.Final.Cut.2004.1080p", UnifiedCategory.Film, "The Final Cut", 2004, null, null, "movie")]
    [InlineData("The.Collection.MULTI.VFF.1080p", UnifiedCategory.Film, "The Collection", null, null, null, "movie")]
    [InlineData("The.French.Dispatch.MULTI.VOSTFR.1080p", UnifiedCategory.Film, "The French Dispatch", null, null, null, "movie")]
    [InlineData("English", UnifiedCategory.Film, "English", null, null, null, "movie")]
    [InlineData("La.Collection", UnifiedCategory.Serie, "La Collection", null, null, null, "series")]
    [InlineData("The.English.Game.S01E02.MULTI.1080p", UnifiedCategory.Serie, "The English Game", null, 1, 2, "series")]
    [InlineData("The.French.Series.S01E01E02.1080p", UnifiedCategory.Serie, "The French Series", null, 1, 1, "series")]
    [InlineData("The.English.Show.2x03.720p", UnifiedCategory.Serie, "The English Show", null, 2, 3, "series")]
    [InlineData("Série.Française.Saison.2.COMPLETE.MULTI.1080p", UnifiedCategory.Serie, "Série Française", null, 2, null, "series")]
    [InlineData("1899.S01E01.1080p", UnifiedCategory.Serie, "1899", null, 1, 1, "series")]
    [InlineData("Space.1999.S01E01.1080p", UnifiedCategory.Serie, "Space 1999", null, 1, 1, "series")]
    [InlineData("2001.A.Space.Odyssey.1968.1080p", UnifiedCategory.Film, "2001 A Space Odyssey", 1968, null, null, "movie")]
    [InlineData("1917.2019.1080p", UnifiedCategory.Film, "1917", 2019, null, null, "movie")]
    [InlineData("Blade.Runner.2049.2017.1080p", UnifiedCategory.Film, "Blade Runner 2049", 2017, null, null, "movie")]
    [InlineData("Blade.Runner.2049", UnifiedCategory.Film, "Blade Runner 2049", null, null, null, "movie")]
    [InlineData("Film.(2035).1080p", UnifiedCategory.Film, "Film", 2035, null, null, "movie")]
    [InlineData("Film.[2035].1080p", UnifiedCategory.Film, "Film", 2035, null, null, "movie")]
    [InlineData("Cyberpunk.2077", UnifiedCategory.JeuWindows, "Cyberpunk 2077", null, null, null, "game")]
    [InlineData("Cyberpunk.2077.PC-GOG", UnifiedCategory.JeuWindows, "Cyberpunk 2077", null, null, null, "game")]
    [InlineData("Cyberpunk.2077.v2.1.MULTI-GRP", UnifiedCategory.JeuWindows, "Cyberpunk 2077", null, null, null, "game")]
    [InlineData("Cyberpunk.2077.2020.v2.1.PC-GOG", UnifiedCategory.JeuWindows, "Cyberpunk 2077", 2020, null, null, "game")]
    [InlineData("FIFA.2026", UnifiedCategory.JeuWindows, "FIFA 2026", null, null, null, "game")]
    [InlineData("FIFA.2026.PC-GOG", UnifiedCategory.JeuWindows, "FIFA 2026", null, null, null, "game")]
    [InlineData("F1.2026", UnifiedCategory.JeuWindows, "F1 2026", null, null, null, "game")]
    [InlineData("F1.2026.v1.2.3.MULTI-RUNE", UnifiedCategory.JeuWindows, "F1 2026", null, null, null, "game")]
    [InlineData("Football.Manager.2025.PC-GOG", UnifiedCategory.JeuWindows, "Football Manager 2025", null, null, null, "game")]
    [InlineData("English.Country.Tune.v1.2.3.PC-GOG", UnifiedCategory.JeuWindows, "English Country Tune", null, null, null, "game")]
    [InlineData("French.Bread.Story.v1.0.PC-GOG", UnifiedCategory.JeuWindows, "French Bread Story", null, null, null, "game")]
    [InlineData("Half-Life.v1.2.3.PC-GOG", UnifiedCategory.JeuWindows, "Half-Life", null, null, null, "game")]
    [InlineData("Half-Life", UnifiedCategory.JeuWindows, "Half-Life", null, null, null, "game")]
    [InlineData("The.Origin.PC-GOG", UnifiedCategory.JeuWindows, "The Origin", null, null, null, "game")]
    [InlineData("The.Collection.v1.2.PC-GOG", UnifiedCategory.JeuWindows, "The Collection", null, null, null, "game")]
    [InlineData("Game.(2024).Deluxe.Edition.v1.0.PC-GOG", UnifiedCategory.JeuWindows, "Game", 2024, null, null, "game")]
    [InlineData("Le.French.Show.2026.11.10.1080p.HDTV", UnifiedCategory.Emission, "Le French Show", 2026, null, null, "series")]
    [InlineData("L.English.Show.10.11.2026.1080p", UnifiedCategory.Emission, "L English Show", 2026, null, null, "series")]
    [InlineData("L.English.Spectacle.(2024).1080p", UnifiedCategory.Spectacle, "L English Spectacle", 2024, null, null, "movie")]
    [InlineData("French.Story.(2023).1080p", UnifiedCategory.Animation, "French Story", 2023, null, null, "movie")]
    [InlineData("[SubsPlease].Blue.Lock.S02E01.1080p", UnifiedCategory.Anime, "Blue Lock", null, 2, 1, "anime")]
    [InlineData("Naruto.001.1080p", UnifiedCategory.Anime, "Naruto 001", null, null, null, "anime")]
    [InlineData("Le.Français.Édition.2026", UnifiedCategory.Book, "Le Français Édition 2026", null, null, null, "book")]
    [InlineData("Mewgenics.(2026).MULTI.1.0.WIN.ISO-TENOKE", UnifiedCategory.JeuWindows, "Mewgenics", 2026, null, null, "game")]
    [InlineData("Sid.Meiers.Civilization.VII.(2025).MULTI.v1.3.2.Linux-RUNE", UnifiedCategory.JeuWindows, "Sid Meiers Civilization VII", 2025, null, null, "game")]
    [InlineData("Lapin.Malin.Maternelle.3.(2000).FRENCH.v3.0.WinMac-NOTAG", UnifiedCategory.JeuWindows, "Lapin Malin Maternelle 3", 2000, null, null, "game")]
    [InlineData("The.Linux.Tycoon.v1.2.3.PC-GOG", UnifiedCategory.JeuWindows, "The Linux Tycoon", null, null, null, "game")]
    [InlineData("The.French.Connection.v1.2.3.PC-GOG", UnifiedCategory.JeuWindows, "The French Connection", null, null, null, "game")]
    [InlineData("Game.2077.1.0.PC-GOG", UnifiedCategory.JeuWindows, "Game 2077", null, null, null, "game")]
    [InlineData("FIFA.2025-2026.PC-GOG", UnifiedCategory.JeuWindows, "FIFA 2025-2026", null, null, null, "game")]
    [InlineData("The.Sims.4.Digital.Deluxe.Edition.2014.MULTI.v1.121.361.1020-LAiN", UnifiedCategory.JeuWindows, "The Sims 4", 2014, null, null, "game")]
    [InlineData("Game.Build.2026.PC-GOG", UnifiedCategory.JeuWindows, "Game", null, null, null, "game")]
    [InlineData("Game.v1.2026.3.PC-GOG", UnifiedCategory.JeuWindows, "Game", null, null, null, "game")]
    [InlineData("Cyberpunk_2077_2020_v2.1_PC-GOG", UnifiedCategory.JeuWindows, "Cyberpunk 2077", 2020, null, null, "game")]
    [InlineData("The.French.Dispatch.2021.WEB.H264-GRP", UnifiedCategory.Film, "The French Dispatch", 2021, null, null, "movie")]
    [InlineData("Saint-Germain.2020.1080p", UnifiedCategory.Film, "Saint-Germain", 2020, null, null, "movie")]
    [InlineData("[REC].(2007).1080p", UnifiedCategory.Film, "[REC]", 2007, null, null, "movie")]
    [InlineData("[English].(2024).1080p", UnifiedCategory.Film, "[English]", 2024, null, null, "movie")]
    [InlineData("Mission.Complete.S01E01.1080p", UnifiedCategory.Serie, "Mission Complete", null, 1, 1, "series")]
    [InlineData("Maths.Intégrale.S01E02.1080p", UnifiedCategory.Serie, "Maths Intégrale", null, 1, 2, "series")]
    public void Parse_UsesContextWithoutLosingTitleWords(string raw, UnifiedCategory category,
        string title, int? year, int? season, int? episode, string mediaType)
    {
        var parsed = new TitleParser().Parse(raw, category);
        Assert.Equal(title, parsed.TitleClean); Assert.Equal(year, parsed.Year);
        Assert.Equal(season, parsed.Season); Assert.Equal(episode, parsed.Episode);
        Assert.Equal(mediaType, parsed.MediaType);
    }
    [Fact]
    public void Parse_KeepsTechnicalFieldsAndGenuineHyphensSeparate()
    {
        var parsed = new TitleParser().Parse("The.French.Dispatch.2021.VFF.1080p.WEB-DL.x264-GRP", UnifiedCategory.Film);
        Assert.Equal("The French Dispatch", parsed.TitleClean); Assert.Equal(2021, parsed.Year);
        Assert.Equal("1080p", parsed.Resolution); Assert.Equal("WEB-DL", parsed.Source);
        Assert.Equal("x264", parsed.Codec); Assert.Equal("GRP", parsed.ReleaseGroup);
        Assert.Null(new TitleParser().Parse("Half-Life", UnifiedCategory.JeuWindows).ReleaseGroup);
    }

    [Theory]
    [InlineData("Le.French.Show.2026.11.10.1080p", "2026-11-10")]
    [InlineData("Le.French.Show.10.11.2026.1080p", "2026-11-10")]
    public void Parse_EmissionPreservesAirDate(string raw, string airDate)
    {
        var parsed = new TitleParser().Parse(raw, UnifiedCategory.Emission);
        Assert.Equal("Le French Show", parsed.TitleClean);
        Assert.Equal(2026, parsed.Year); Assert.Equal(airDate, parsed.AirDate);
    }

}
