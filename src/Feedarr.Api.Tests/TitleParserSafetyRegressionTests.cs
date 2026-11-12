using Feedarr.Api.Models;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Tests;

public sealed class TitleParserSafetyRegressionTests
{
    // All 18 confirmed corpus regressions; private signatures replaced with TEAM.
    public static IEnumerable<object[]> Cases()
    {
        yield return new object[] { "Salvation.-.Echoes.of.War.[Pre-Installed][FRENCH.SUBS][Ver.1.2.5878][2026]", UnifiedCategory.JeuWindows, new ParsedTitle { AirDate = null, Codec = null, Episode = null, MediaType = "game", ReleaseGroup = null, Resolution = null, Season = null, Source = null, TitleClean = "Salvation - Echoes of War [Pre-Installed][FRENCH SUBS]", Year = 2026 } };
        yield return new object[] { "Gears.Of.War.E-Day.ISO.WINDOWS.MULTi-TEAM", UnifiedCategory.JeuWindows, new ParsedTitle { AirDate = null, Codec = null, Episode = null, MediaType = "game", ReleaseGroup = "TEAM", Resolution = null, Season = null, Source = null, TitleClean = "Gears Of War E-Day", Year = null } };
        yield return new object[] { "Le.Livre.De.La.Jungle.1967.MULTi.VFF.1080p.BLURAY.X264.POPHD.THE.JUNGLE-BOOK.FRENCH", UnifiedCategory.Animation, new ParsedTitle { AirDate = null, Codec = "x264", Episode = null, MediaType = "movie", ReleaseGroup = null, Resolution = "1080p", Season = null, Source = "BluRay", TitleClean = "Le Livre De La Jungle", Year = 1967 } };
        yield return new object[] { "Simply.Red.Holding.Back.the.Years.Live.in.Santiago..2026.VO.1080p.BluRay.x264.E-AC-3.5.1-TEAM", UnifiedCategory.Film, new ParsedTitle { AirDate = null, Codec = "x264", Episode = null, MediaType = "movie", ReleaseGroup = "TEAM", Resolution = "1080p", Season = null, Source = "BluRay", TitleClean = "Simply Red Holding Back the Years Live in Santiago", Year = 2026 } };
        yield return new object[] { "Fritchii.build.5.6.Lichii.nx2.build.130.Firmware.23.0.1.ProdKeys.-v22.5.0.Switch-TEAM", UnifiedCategory.JeuWindows, new ParsedTitle { AirDate = null, Codec = null, Episode = null, MediaType = "game", ReleaseGroup = "TEAM", Resolution = null, Season = null, Source = null, TitleClean = "Fritchii build 5 6 Lichii nx2 build 130 Firmware 23 0 1 ProdKeys", Year = null } };
        yield return new object[] { "Handling.The.Undead.2024.VOSTFR.1080P.Webrip.x265.E-AC-3.5.1-TEAM", UnifiedCategory.Film, new ParsedTitle { AirDate = null, Codec = "x265", Episode = null, MediaType = "movie", ReleaseGroup = "TEAM", Resolution = "1080p", Season = null, Source = "WEBRip", TitleClean = "Handling The Undead", Year = 2024 } };
        yield return new object[] { "Les.Croods.2013.MULTi.VFQ.VO.VFF.1080p.BLURAY.X264.POPHD.THE-CROODS.FRENCH", UnifiedCategory.Animation, new ParsedTitle { AirDate = null, Codec = "x264", Episode = null, MediaType = "movie", ReleaseGroup = null, Resolution = "1080p", Season = null, Source = "BluRay", TitleClean = "Les Croods", Year = 2013 } };
        yield return new object[] { "Les.Croods.2.Une.Nouvelle.Ere.2020.MULTi.VFQ.1080p.BLURAY.X264.POPHD.THE.CROODS.A.NEW-AGE.FRENCH", UnifiedCategory.Animation, new ParsedTitle { AirDate = null, Codec = "x264", Episode = null, MediaType = "movie", ReleaseGroup = null, Resolution = "1080p", Season = null, Source = "BluRay", TitleClean = "Les Croods 2 Une Nouvelle Ere", Year = 2020 } };
        yield return new object[] { "Nature.Predatrice.2026.MULTI.1080P.Nf.x265.E-AC-3.5.1-TEAM", UnifiedCategory.Film, new ParsedTitle { AirDate = null, Codec = "x265", Episode = null, MediaType = "movie", ReleaseGroup = "TEAM", Resolution = "1080p", Season = null, Source = null, TitleClean = "Nature Predatrice", Year = 2026 } };
        yield return new object[] { "MSFS.2024.AzurPoly.-.Rafale-C.v1.1.0-TEAM", UnifiedCategory.JeuWindows, new ParsedTitle { AirDate = null, Codec = null, Episode = null, MediaType = "game", ReleaseGroup = "TEAM", Resolution = null, Season = null, Source = null, TitleClean = "MSFS 2024 AzurPoly - Rafale-C", Year = null } };
        yield return new object[] { "[scoot] Night on the Galactic Railroad [BD 1080p HEVC Opus] [Dual Audio] [1617AD5B].mkv", UnifiedCategory.Animation, new ParsedTitle { AirDate = null, Codec = "x265", Episode = null, MediaType = "movie", ReleaseGroup = null, Resolution = "1080p", Season = null, Source = null, TitleClean = "[scoot] Night on the Galactic Railroad", Year = null } };
        yield return new object[] { "Moonlighter.2.The.Endless.Vault.[Pre-Installed][Portable][MULTI][UPDATED][V1.0.42.3][2026]", UnifiedCategory.JeuWindows, new ParsedTitle { AirDate = null, Codec = null, Episode = null, MediaType = "game", ReleaseGroup = null, Resolution = null, Season = null, Source = null, TitleClean = "Moonlighter 2 The Endless Vault [Pre-Installed][Portable][MULTI][UPDATED]", Year = 2026 } };
        yield return new object[] { "Pour.Zelda.Ocarina.Of.Time.Switch.2.sur.Windows.Lichii.nx2.build.125.cbb512e.Firmware.23.0.1.ProdKeys.-v22.5.0.Switch-TEAM", UnifiedCategory.JeuWindows, new ParsedTitle { AirDate = null, Codec = null, Episode = null, MediaType = "game", ReleaseGroup = "TEAM", Resolution = null, Season = null, Source = null, TitleClean = "Pour Zelda Ocarina Of Time Switch 2 sur Windows Lichii nx2 build 125 cbb512e Firmware 23 0 1 ProdKeys", Year = null } };
        yield return new object[] { "Call.of.Duty.4.Modern.Warfare.Remastered.FRENCH.MULTI5.x86_64-TEAM", UnifiedCategory.JeuWindows, new ParsedTitle { AirDate = null, Codec = null, Episode = null, MediaType = "game", ReleaseGroup = "TEAM", Resolution = null, Season = null, Source = null, TitleClean = "Call of Duty 4 Modern Warfare Remastered", Year = null } };
        yield return new object[] { "Les.Contes.De.Terremer.2006.BLURAY.1080p.HEVCX26510BiT.MULTi.VFF.51.AC3.VO.51.AC3.V2.PUNiSHER694.TALES.FROM.EARTHSEA.GED-SENKi.FRENCH", UnifiedCategory.Animation, new ParsedTitle { AirDate = null, Codec = "AC3", Episode = null, MediaType = "movie", ReleaseGroup = null, Resolution = "1080p", Season = null, Source = "BluRay", TitleClean = "Les Contes De Terremer", Year = 2006 } };
        yield return new object[] { "Stargate.Universe.INTEGRALE.TRUEFRENCH.1080p.BluRay.AV1.AC-3-TEAM", UnifiedCategory.Serie, new ParsedTitle { AirDate = null, Codec = "AV1", Episode = null, MediaType = "series", ReleaseGroup = "TEAM", Resolution = "1080p", Season = null, Source = "BluRay", TitleClean = "Stargate Universe", Year = null } };
        yield return new object[] { "Les.Cinq.Legendes.2012.MULTi.VFQ.VO.VFF.1080p.BLURAY.X264.POPHD.RiSE.OF.THE-GUARDiANS.FRENCH", UnifiedCategory.Animation, new ParsedTitle { AirDate = null, Codec = "x264", Episode = null, MediaType = "movie", ReleaseGroup = null, Resolution = "1080p", Season = null, Source = "BluRay", TitleClean = "Les Cinq Legendes", Year = 2012 } };
        yield return new object[] { "Les.Douze.Travaux.D.Asterix.1976.FRENCH.720p.BLURAY.X264.TSUNAMi.LES.12.TRAVAUX-DASTERiX", UnifiedCategory.Animation, new ParsedTitle { AirDate = null, Codec = "x264", Episode = null, MediaType = "movie", ReleaseGroup = null, Resolution = "720p", Season = null, Source = "BluRay", TitleClean = "Les Douze Travaux D Asterix", Year = 1976 } };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Parse_ConfirmedSafetyRegression(string raw, UnifiedCategory category, ParsedTitle expected)
    {
        var actual = new TitleParser().Parse(raw, category);
        Assert.Equal(expected.AirDate, actual.AirDate);
        Assert.Equal(expected.Codec, actual.Codec);
        Assert.Equal(expected.Episode, actual.Episode);
        Assert.Equal(expected.MediaType, actual.MediaType);
        Assert.Equal(expected.ReleaseGroup, actual.ReleaseGroup);
        Assert.Equal(expected.Resolution, actual.Resolution);
        Assert.Equal(expected.Season, actual.Season);
        Assert.Equal(expected.Source, actual.Source);
        Assert.Equal(expected.TitleClean, actual.TitleClean);
        Assert.Equal(expected.Year, actual.Year);
    }

    [Theory]
    [InlineData("Game.Title.Windows.MULTi.COMPLETE-TEAM", UnifiedCategory.JeuWindows)]
    [InlineData("Game.Title.MULTi17.PC.Conversion-TEAM", UnifiedCategory.JeuWindows)]
    [InlineData("Game Title (V1.0.0) EUR MULTi Switch NPS - TEAM", UnifiedCategory.JeuWindows)]
    [InlineData("Game.Title.FRENCH.MULTI5.x86_64-TEAM", UnifiedCategory.JeuWindows)]
    [InlineData("Film.2026.VO.1080p.WEB.AAC.2.0x265-TEAM", UnifiedCategory.Film)]
    public void Parse_GroupEvidenceRetainsDistributionSignatures(string raw, UnifiedCategory category)
        => Assert.Equal("TEAM", new TitleParser().Parse(raw, category).ReleaseGroup);

    [Theory]
    [InlineData("Film.2020.1080p.WEB.x264.THE-ALTERNATE")]
    [InlineData("Film.2020.1080p.WEB.x264.UnknownDistribution-TEAM")]
    public void Parse_FreeTextCannotInventATerminalGroup(string raw)
        => Assert.Null(new TitleParser().Parse(raw, UnifiedCategory.Film).ReleaseGroup);

    [Theory]
    [InlineData("Godzilla.Destroy.All.Monsters.Melee.Remastered-RUNE", "Godzilla Destroy All Monsters Melee Remastered")]
    [InlineData("DreamWorks.Shrek.2.Remastered-RUNE", "DreamWorks Shrek 2 Remastered")]
    [InlineData("Game.Remastered-CODEX", "Game Remastered")]
    [InlineData("Game.(Remastered.+.Bonus.OST)-[CODEX.Repack]", "Game")]
    public void Parse_EditionPrefixIsDistinctFromClosedDistributionBlock(string raw, string title)
        => Assert.Equal(title, new TitleParser().Parse(raw, UnifiedCategory.JeuWindows).TitleClean);

}
