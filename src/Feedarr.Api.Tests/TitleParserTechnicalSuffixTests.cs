using Feedarr.Api.Models;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Tests;

public sealed class TitleParserTechnicalSuffixTests
{
    // Resolution alone is intentionally insufficient to disambiguate a title number.
    [Theory]
    [InlineData("Argo.2012.H.264-GRP.MKV", UnifiedCategory.Film, "Argo", 2012, "GRP.MKV", null, null, "x264", null, null, "movie")]
    [InlineData("Argo.2012.H.265-GRP.MKV", UnifiedCategory.Film, "Argo", 2012, "GRP.MKV", null, null, "x265", null, null, "movie")]
    [InlineData("Blade.Runner.2049.1080p-GRP", UnifiedCategory.Film, "Blade Runner 2049", null, "GRP", "1080p", null, null, null, null, "movie")]
    [InlineData("Film.2020.4k.mkv", UnifiedCategory.Film, "Film 2020", null, null, "2160p", null, null, null, null, "movie")]
    [InlineData("Movie.TRUEFRENCH.DVDRip-X", UnifiedCategory.Film, "Movie", null, null, null, "DVDRip", null, null, null, "movie")]
    [InlineData("[SubsPlease].Show.-.01.(1080p).[ABC12345]", UnifiedCategory.Anime, "Show - 01", null, null, "1080p", null, null, null, null, "anime")]
    [InlineData("Movie.(Director.Cut).2020.1080p", UnifiedCategory.Film, "Movie", 2020, null, "1080p", null, null, null, null, "movie")]
    [InlineData("Argo.2012.DTS.1080p.BluRay-GRP", UnifiedCategory.Film, "Argo", 2012, "GRP", "1080p", "BluRay", null, null, null, "movie")]
    [InlineData("Argo.2012.DTS-HD.MA.5.1.1080p.BluRay-GRP", UnifiedCategory.Film, "Argo", 2012, "GRP", "1080p", "BluRay", null, null, null, "movie")]
    [InlineData("Argo.2012.10bit.1080p.BluRay-GRP", UnifiedCategory.Film, "Argo", 2012, "GRP", "1080p", "BluRay", null, null, null, "movie")]
    [InlineData("Argo.2012.5.1.1080p.BluRay-GRP", UnifiedCategory.Film, "Argo", 2012, "GRP", "1080p", "BluRay", null, null, null, "movie")]
    [InlineData("Argo.2012.UHD.HDR.1080p.BluRay-GRP", UnifiedCategory.Film, "Argo", 2012, "GRP", "1080p", "BluRay", null, null, null, "movie")]
    [InlineData("Argo.2012.PROPER.1080p.BluRay-GRP", UnifiedCategory.Film, "Argo", 2012, "GRP", "1080p", "BluRay", null, null, null, "movie")]
    [InlineData("Argo.2012.AAC.2.0.1080p.BluRay-GRP", UnifiedCategory.Film, "Argo", 2012, "GRP", "1080p", "BluRay", null, null, null, "movie")]
    [InlineData("Argo.2012.DDP.5.1.1080p.BluRay-GRP", UnifiedCategory.Film, "Argo", 2012, "GRP", "1080p", "BluRay", null, null, null, "movie")]
    [InlineData("Argo.2012.TrueHD.7.1.Atmos.1080p.BluRay-GRP", UnifiedCategory.Film, "Argo", 2012, "GRP", "1080p", "BluRay", null, null, null, "movie")]
    [InlineData("Argo.2012.AVC-GRP", UnifiedCategory.Film, "Argo", 2012, "GRP", null, null, "x264", null, null, "movie")]
    [InlineData("Argo.2012.Blu-ray.AVC-GRP", UnifiedCategory.Film, "Argo", 2012, "GRP", null, "BluRay", "x264", null, null, "movie")]
    [InlineData("Argo.2012.XviD-GRP", UnifiedCategory.Film, "Argo", 2012, "GRP", null, null, null, null, null, "movie")]
    [InlineData("Argo.FRENCH.1080p.WEB-DL.x264-GRP", UnifiedCategory.Film, "Argo", null, "GRP", "1080p", "WEB-DL", "x264", null, null, "movie")]
    [InlineData("Artist.Show.2020.DTS.1080p", UnifiedCategory.Spectacle, "Artist Show", 2020, null, "1080p", null, null, null, null, "movie")]
    [InlineData("Movie.2020.DTS.1080p", UnifiedCategory.Animation, "Movie", 2020, null, "1080p", null, null, null, null, "movie")]
    [InlineData("Portal.2.Update.v1.2.3-CODEX", UnifiedCategory.JeuWindows, "Portal 2", null, "CODEX", null, null, null, null, null, "game")]
    [InlineData("Portal.2.Hotfix.v1.2-CODEX", UnifiedCategory.JeuWindows, "Portal 2", null, "CODEX", null, null, null, null, null, "game")]
    [InlineData("Portal.2.REPACK.v1.2-CODEX", UnifiedCategory.JeuWindows, "Portal 2", null, "CODEX", null, null, null, null, null, "game")]
    [InlineData("Portal.2.Early.Access.v1.2-CODEX", UnifiedCategory.JeuWindows, "Portal 2", null, "CODEX", null, null, null, null, null, "game")]
    [InlineData("Portal.2.Bonus.Content.v1.2-CODEX", UnifiedCategory.JeuWindows, "Portal 2", null, "CODEX", null, null, null, null, null, "game")]
    [InlineData("Portal.2.OST.v1.2-CODEX", UnifiedCategory.JeuWindows, "Portal 2", null, "CODEX", null, null, null, null, null, "game")]
    [InlineData("Portal.2.REPACK-GRP", UnifiedCategory.JeuWindows, "Portal 2", null, "GRP", null, null, null, null, null, "game")]
    [InlineData("Game.1.0.PC-GOG", UnifiedCategory.JeuWindows, "Game", null, "GOG", null, null, null, null, null, "game")]
    [InlineData("Game.1.0-GRP", UnifiedCategory.JeuWindows, "Game 1 0-GRP", null, null, null, null, null, null, null, "game")]
    [InlineData("Cyberpunk.2077.Update.v2.1-CODEX", UnifiedCategory.JeuWindows, "Cyberpunk 2077", null, "CODEX", null, null, null, null, null, "game")]
    [InlineData("F1.2026.Update.v1.2-CODEX", UnifiedCategory.JeuWindows, "F1 2026", null, "CODEX", null, null, null, null, null, "game")]
    [InlineData("[Fansub].Show.S01E02.1080p", UnifiedCategory.Anime, "Show", null, null, "1080p", null, null, 1, 2, "anime")]
    [InlineData("[REC].(2007).1080p", UnifiedCategory.Film, "[REC]", 2007, null, "1080p", null, null, null, null, "movie")]
    [InlineData("[Fansub].Show", UnifiedCategory.Anime, "[Fansub] Show", null, null, null, null, null, null, null, "anime")]
    [InlineData("The.Office.S01E02.2005.1080p", UnifiedCategory.Serie, "The Office", 2005, null, "1080p", null, null, 1, 2, "series")]
    [InlineData("The.Office.2005.S01E02", UnifiedCategory.Serie, "The Office 2005", null, null, null, null, null, 1, 2, "series")]
    [InlineData("The.Office.(2005).S01E02", UnifiedCategory.Serie, "The Office", 2005, null, null, null, null, 1, 2, "series")]
    [InlineData("Space.1999", UnifiedCategory.Serie, "Space 1999", null, null, null, null, null, null, null, "series")]
    [InlineData("Space.1999.S01E01.1080p", UnifiedCategory.Serie, "Space 1999", null, null, "1080p", null, null, 1, 1, "series")]
    [InlineData("Blade.Runner.2049.1080p", UnifiedCategory.Film, "Blade Runner 2049", null, null, "1080p", null, null, null, null, "movie")]
    [InlineData("Film.2020.1080p", UnifiedCategory.Film, "Film 2020", null, null, "1080p", null, null, null, null, "movie")]
    [InlineData("Blade.Runner.2049.2017.1080p", UnifiedCategory.Film, "Blade Runner 2049", 2017, null, "1080p", null, null, null, null, "movie")]
    [InlineData("Film.(2035).1080p", UnifiedCategory.Film, "Film", 2035, null, "1080p", null, null, null, null, "movie")]
    [InlineData("The.DTS.Story.1080p.WEB-DL.x264-GRP", UnifiedCategory.Film, "The DTS Story", null, "GRP", "1080p", "WEB-DL", "x264", null, null, "movie")]
    [InlineData("The.AVC.Story.1080p.WEB-DL.x264-GRP", UnifiedCategory.Film, "The AVC Story", null, "GRP", "1080p", "WEB-DL", "x264", null, null, "movie")]
    [InlineData("The.French.Dispatch.1080p.WEB-DL.x264-GRP", UnifiedCategory.Film, "The French Dispatch", null, "GRP", "1080p", "WEB-DL", "x264", null, null, "movie")]
    [InlineData("The.English.Patient.1080p.WEB-DL.x264-GRP", UnifiedCategory.Film, "The English Patient", null, "GRP", "1080p", "WEB-DL", "x264", null, null, "movie")]
    [InlineData("Johnny.English.1080p.WEB-DL.x264-GRP", UnifiedCategory.Film, "Johnny English", null, "GRP", "1080p", "WEB-DL", "x264", null, null, "movie")]
    [InlineData("The.HDR.Story.1080p.WEB-DL.x264-GRP", UnifiedCategory.Film, "The HDR Story", null, "GRP", "1080p", "WEB-DL", "x264", null, null, "movie")]
    [InlineData("The.Update.Story.v1.2-CODEX", UnifiedCategory.JeuWindows, "The Update Story", null, "CODEX", null, null, null, null, null, "game")]
    [InlineData("The.Linux.Tycoon.v1.2-CODEX", UnifiedCategory.JeuWindows, "The Linux Tycoon", null, "CODEX", null, null, null, null, null, "game")]
    [InlineData("The.French.Connection.v1.2-CODEX", UnifiedCategory.JeuWindows, "The French Connection", null, "CODEX", null, null, null, null, null, "game")]
    [InlineData("Game.Deluxe.Edition.The.Return.v1.2-CODEX", UnifiedCategory.JeuWindows, "Game Deluxe Edition The Return", null, "CODEX", null, null, null, null, null, "game")]
    [InlineData("Game.DLC.Quest.v1.2-CODEX", UnifiedCategory.JeuWindows, "Game DLC Quest", null, "CODEX", null, null, null, null, null, "game")]
    [InlineData("Half-Life", UnifiedCategory.JeuWindows, "Half-Life", null, null, null, null, null, null, null, "game")]
    [InlineData("Show.S01E02.2024.06.01.1080p", UnifiedCategory.Serie, "Show", null, null, "1080p", null, null, 1, 2, "series")]
    [InlineData("Show.S01E02.v1.2024.1080p", UnifiedCategory.Serie, "Show", null, null, "1080p", null, null, 1, 2, "series")]
    public void Parse_AuditRegressionsAndTitleGuards(string raw, UnifiedCategory category, string title,
        int? year, string? group, string? resolution, string? source, string? codec,
        int? season, int? episode, string mediaType)
    {
        var parsed = new TitleParser().Parse(raw, category);
        Assert.Equal(title, parsed.TitleClean);
        Assert.Equal(year, parsed.Year);
        Assert.Equal(group, parsed.ReleaseGroup);
        Assert.Equal(resolution, parsed.Resolution);
        Assert.Equal(source, parsed.Source);
        Assert.Equal(codec, parsed.Codec);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episode, parsed.Episode);
        Assert.Null(parsed.AirDate);
        Assert.Equal(mediaType, parsed.MediaType);
    }
}
