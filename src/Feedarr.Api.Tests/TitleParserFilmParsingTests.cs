using Feedarr.Api.Models;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Tests;

public sealed class TitleParserFilmParsingTests
{
    private readonly TitleParser _parser = new();

    [Theory]
    [InlineData(
        UnifiedCategory.Film,
        "Goldorak (2013) iNTEGRALE REEDITION MULTi VFF 1080p DVDRip AC3 2.0 x264-GuS2SuG",
        "Goldorak")]
    [InlineData(
        UnifiedCategory.Film,
        "Das.Boot.Directors.Cut.2.Multi.VFF.1080p.Bluray-Remux.AVC-SNBX.mkv",
        "Das Boot")]
    [InlineData(
        UnifiedCategory.Animation,
        "Macross Plus OAV (1994) Multi 1080p Full BluRay AVC-NoTag",
        "Macross Plus")]
    public void Parse_Video_CleansKnownNoisyPatterns(UnifiedCategory category, string raw, string expectedTitleClean)
    {
        var parsed = _parser.Parse(raw, category);

        Assert.Equal(expectedTitleClean, parsed.TitleClean);
    }
}
