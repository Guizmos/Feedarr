using Feedarr.Api.Models;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Tests;

public sealed class TitleParserCategoryPolicyTests
{
    [Theory]
    [InlineData("Film.2020.1080p.WEB-DL.x264-TEAM", UnifiedCategory.Film, "Film", 2020, null, null, null, "movie")]
    [InlineData("Blade.Runner.2049.1080p", UnifiedCategory.Film, "Blade Runner 2049", null, null, null, null, "movie")]
    [InlineData("Space.1999", UnifiedCategory.Film, "Space 1999", null, null, null, null, "movie")]
    [InlineData("Show.S01E02.2005.1080p.WEB-DL.x264-TEAM", UnifiedCategory.Serie, "Show", 2005, 1, 2, null, "series")]
    [InlineData("[Fansub].Show.S01E02.1080p", UnifiedCategory.Anime, "Show", null, 1, 2, null, "anime")]
    [InlineData("Show.S01E02.1080p.WEB-DL.x264-TEAM", UnifiedCategory.Animation, "Show S01E02", null, null, null, null, "movie")]
    [InlineData("Show.2026.11.12.S01E02.1080p", UnifiedCategory.Emission, "Show", 2026, null, null, "2026-11-12", "series")]
    [InlineData("Show.S03E04.1080p", UnifiedCategory.Emission, "Show", null, 3, 4, null, "series")]
    [InlineData("Portal.2.Update.v1.2.3-CODEX", UnifiedCategory.JeuWindows, "Portal 2", null, null, null, null, "game")]
    [InlineData("F1.2026.Update.v1.2.3-CODEX", UnifiedCategory.JeuWindows, "F1 2026", null, null, null, null, "game")]
    [InlineData("Cyberpunk.2077.v2.5.WIN.PREACTIVED-TEAM", UnifiedCategory.JeuWindows, "Cyberpunk 2077", null, null, null, null, "game")]
    [InlineData("Performance.2024.VFF.1080p.WEB-DL.x264-TEAM", UnifiedCategory.Spectacle, "Performance", 2024, null, null, null, "movie")]
    public void Parse_CategoryPoliciesPreserveDifferentIdentitySemantics(
        string raw, UnifiedCategory category, string title, int? year, int? season,
        int? episode, string? airDate, string mediaType)
    {
        var actual = new TitleParser().Parse(raw, category);
        Assert.Equal(title, actual.TitleClean);
        Assert.Equal(year, actual.Year);
        Assert.Equal(season, actual.Season);
        Assert.Equal(episode, actual.Episode);
        Assert.Equal(airDate, actual.AirDate);
        Assert.Equal(mediaType, actual.MediaType);
    }

    [Fact]
    public void Parse_SharedPoliciesDoNotLeakStateAcrossConcurrentCategories()
    {
        var parser = new TitleParser();
        Parallel.For(0, 96, i =>
        {
            var broadcast = parser.Parse("Show.2026.11.12.S01E02.1080p", UnifiedCategory.Emission);
            var game = parser.Parse("F1.2026.Update.v1.2.3-CODEX", UnifiedCategory.JeuWindows);
            var episode = parser.Parse("Show.S03E04.1080p", UnifiedCategory.Serie);
            Assert.Equal("2026-11-12", broadcast.AirDate);
            Assert.Null(broadcast.Season);
            Assert.Equal("F1 2026", game.TitleClean);
            Assert.Null(game.Year);
            Assert.Null(game.AirDate);
            Assert.Equal(3, episode.Season);
            Assert.Equal(4, episode.Episode);
            Assert.Null(episode.Year);
            Assert.Null(episode.AirDate);
            // ParsedTitle stays a per-call mutable DTO, never a cached shared result.
            game.TitleClean = "user-owned result";
            Assert.Equal("F1 2026", parser.Parse("F1.2026.Update.v1.2.3-CODEX", UnifiedCategory.JeuWindows).TitleClean);
        });
    }
}
