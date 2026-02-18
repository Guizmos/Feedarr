using Feedarr.Api.Models;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Tests;

public sealed class TitleParserSeriesParsingTests
{
    private readonly TitleParser _parser = new();

    [Fact]
    public void Parse_Series_RemovesPalNoise()
    {
        const string raw = "Yu-Gi-Oh!.Duel.Monsters.iNTEGRALE.FRENCH.576p.PAL.DVDRip.AC3.H264-DragonMax";

        var parsed = _parser.Parse(raw, UnifiedCategory.Serie);

        Assert.Equal("Yu-Gi-Oh! Duel Monsters", parsed.TitleClean);
    }
}
