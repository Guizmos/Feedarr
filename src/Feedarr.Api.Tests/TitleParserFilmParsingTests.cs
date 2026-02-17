using Feedarr.Api.Models;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Tests;

public sealed class TitleParserFilmParsingTests
{
    private readonly TitleParser _parser = new();

    [Fact]
    public void Parse_Film_RemovesReeditionNoise()
    {
        const string raw = "Goldorak (2013) iNTEGRALE REEDITION MULTi VFF 1080p DVDRip AC3 2.0 x264-GuS2SuG";

        var parsed = _parser.Parse(raw, UnifiedCategory.Film);

        Assert.Equal("Goldorak", parsed.TitleClean);
    }
}
