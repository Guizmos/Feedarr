using Feedarr.Api.Models;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Tests;

public sealed class TitleParserGameParsingTests
{
    private readonly TitleParser _parser = new();

    // An unbracketed four-digit game number is retained unless independent date evidence exists.
    [Theory]
    [InlineData(
        "Machine Tower 2984: Supporter Pack-FitGirl Repack (v1.0.0) + (Bonus OST)",
        "Machine Tower 2984")]
    [InlineData(
        "Resident Evil 4 (2023): Gold Edition v1.5.0.0 (Denuvoless) + 26 DLCs + 3 Bonus OSTs - FitGirl Repack",
        "Resident Evil 4")]
    [InlineData(
        "Lapin.Malin.Maternelle.3.2000.FRENCH.v3.0.WinMac.PREACTIVE-NOTAG",
        "Lapin Malin Maternelle 3 2000")]
    [InlineData(
        "Mewgenics.2026.MULTI.1.0.WIN.ISO-TENOKE",
        "Mewgenics 2026")]
    [InlineData(
        "Sid.Meiers.Civilization.VII.2025.MULTI.v1.3.2.+29.DLC.+Multiplayer.Linux.PREACTIVE-LinuxRuleZ",
        "Sid Meiers Civilization VII 2025")]
    [InlineData(
        "The.Sims.4.Digital.Deluxe.Edition.2014.MULTI.v1.121.361.1020.+All.DLC.Linux.PREACTIVE.anadius-LAiN",
        "The Sims 4")]
    public void Parse_GameTitles_CleansKnownNoisyPatterns(string raw, string expectedTitleClean)
    {
        var parsed = _parser.Parse(raw, UnifiedCategory.JeuWindows);

        Assert.Equal(expectedTitleClean, parsed.TitleClean);
    }
}
