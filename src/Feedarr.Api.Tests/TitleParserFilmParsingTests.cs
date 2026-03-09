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
    [InlineData(
        UnifiedCategory.Film,
        "Argo (2012) Hybrid MULTi VFF 2160p 10bit 4KLight DV HDR BluRay DDP 5.1 x265-SchOzzZ.mkv",
        "Argo")]
    [InlineData(
        UnifiedCategory.Film,
        "Les.Tuches.2011-2025.COLLECTiON.FRENCH.1080p.WEB.H265-TyHD",
        "Les Tuches")]
    [InlineData(
        UnifiedCategory.Film,
        "Gravity (2013) Diamond Edition MULTi VF2 BluRay 1080p TrueHD 7.1 Atmos x265-JTR",
        "Gravity")]
    [InlineData(
        UnifiedCategory.Film,
        "KUBO.and.the.last.stings.2016Multi.VF2.1080p.BluRay.3D.SBS.AC3.x264-OLIS",
        "KUBO and the last stings")]
    public void Parse_Video_CleansKnownNoisyPatterns(UnifiedCategory category, string raw, string expectedTitleClean)
    {
        var parsed = _parser.Parse(raw, category);

        Assert.Equal(expectedTitleClean, parsed.TitleClean);
    }
}
