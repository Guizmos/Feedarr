using Feedarr.Api.Models;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Tests;

public sealed class TitleParserSeriesParsingTests
{
    private readonly TitleParser _parser = new();

    [Theory]
    [InlineData(
        "Yu-Gi-Oh!.Duel.Monsters.iNTEGRALE.FRENCH.576p.PAL.DVDRip.AC3.H264-DragonMax",
        "Yu-Gi-Oh! Duel Monsters")]
    [InlineData(
        "J.irai.dormir.chez.vous.2005.2019.INTEGRALE.VOF.480p.WEBRip.AAC.2.0.x264-NOTAG",
        "J irai dormir chez vous")]
    [InlineData(
        "Munch.2016COMPLETE.FRENCH.VOF.1080p.WEB.H264-THESYNDiCATE",
        "Munch")]
    [InlineData(
        "Star.Trek.1966.iNTEGRALE.RESTORED.MULTi.VFF.1080p.BluRay.HDLightDD2.0.x264-Thederviche",
        "Star Trek")]
    [InlineData(
        "Medium.2005.INTERGALE.MULTI.1080p.WEBRip.10bits.x265.DD2.0.DD5.1-Jarod",
        "Medium")]
    [InlineData(
        "Columbo.COMPLETE.DUAL.1080i.BLURAY.REMUX.AVC-GLaDOS",
        "Columbo")]
    [InlineData(
        "She-Hulk.Attorney.at.Law.S01MULTi.1080p.WEBRip.x265-KFL",
        "She-Hulk Attorney at Law")]
    [InlineData(
        "Man.Seeking.Woman.INTEGRALE.VOST.FR.EN.720p.HDTV.AC3.x264-STEGNER",
        "Man Seeking Woman")]
    [InlineData(
        "Das.Boot.Directors.Cut.2.Multi.VFF.1080p.Bluray-Remux.AVC-SNBX.mkv",
        "Das Boot")]
    [InlineData(
        "Oedo.Fire.Slayer.-The.Legend.of.Phoenix-.S01E08v2.VOSTFR.720p.WEB.x264.AAC.-Tsundere-Raws.CR",
        "Oedo Fire Slayer - The Legend of Phoenix")]
    [InlineData(
        "Automan.Integrale.French.Tvrip.Xvid.Mp3-NoTag",
        "Automan")]
    [InlineData(
        "Romanzo.Criminale.2008.CUSTOM.S01.MULTI.VFF.1080p.BluRay.AC3.5.1.x264-SQUEEZE",
        "Romanzo Criminale")]
    public void Parse_Series_RemovesKnownNoisePatterns(string raw, string expectedTitleClean)
    {
        var parsed = _parser.Parse(raw, UnifiedCategory.Serie);

        Assert.Equal(expectedTitleClean, parsed.TitleClean);
    }
}
