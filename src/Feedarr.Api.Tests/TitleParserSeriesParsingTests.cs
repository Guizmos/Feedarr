using Feedarr.Api.Models;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Tests;

public sealed class TitleParserSeriesParsingTests
{
    private readonly TitleParser _parser = new();

    [Theory]
    [InlineData(
        "Dragon.Raja.The.Blazing.Dawn.Japanese.ver.S02E19.VOSTFR.1080p.WEBRip.x265.10bit.AAC-TLC",
        "Dragon Raja The Blazing Dawn")]
    [InlineData(
        "WWE.RAW.16.02.2026.FRENCH.1080p.WEB.x264-COLL3CTiF",
        "WWE RAW")]
    [InlineData(
        "Madam.Secretary.INTEGRALE.FRENCH EN.1080p.WEB.H265.E-AC-3-TyHD",
        "Madam Secretary")]
    [InlineData(
        "His.Dark.Materials.2019.INTEGRAL.VO.MULTISUB.WEB.4K.x265.DVP8-NOGRP",
        "His Dark Materials")]
    [InlineData(
        "Yu-Gi-Oh!.Duel.Monsters.iNTEGRALE.FRENCH.576p.PAL.DVDRip.AC3.H264-DragonMax",
        "Yu-Gi-Oh! Duel Monsters")]
    public void Parse_Series_CleansKnownNoisyPatterns(string raw, string expectedTitleClean)
    {
        var parsed = _parser.Parse(raw, UnifiedCategory.Serie);

        Assert.Equal(expectedTitleClean, parsed.TitleClean);
    }
}
