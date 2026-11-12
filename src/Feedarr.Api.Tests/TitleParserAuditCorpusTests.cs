using System.Text.Json;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Tests;

public sealed class TitleParserAuditCorpusTests
{
    // The 174 audit inputs include historical fixtures and additional diagnostic formats.
    // Expected changes were reviewed against the audit and the agreed resolution-only policy.
    public static IEnumerable<object[]> Cases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Golden", "parser-lot5-p0.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(174, document.RootElement.GetArrayLength());
        var index = 0;
        foreach (var row in document.RootElement.EnumerateArray())
            yield return new object[]
            {
                index++,
                row.GetProperty("Raw").GetString()!,
                Enum.Parse<UnifiedCategory>(row.GetProperty("Category").GetString()!),
                row.GetProperty("Expected").GetRawText()
            };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Parse_MatchesReviewedStructuredCorpus(int caseIndex, string raw, UnifiedCategory category, string expectedJson)
    {
        Assert.InRange(caseIndex, 0, 173);
        var expected = JsonSerializer.Deserialize<ParsedTitle>(expectedJson)!;
        var actual = new TitleParser().Parse(raw, category);
        Assert.Equal(expected.TitleClean, actual.TitleClean);
        Assert.Equal(expected.Year, actual.Year);
        Assert.Equal(expected.Season, actual.Season);
        Assert.Equal(expected.Episode, actual.Episode);
        Assert.Equal(expected.AirDate, actual.AirDate);
        Assert.Equal(expected.Resolution, actual.Resolution);
        Assert.Equal(expected.Source, actual.Source);
        Assert.Equal(expected.Codec, actual.Codec);
        Assert.Equal(expected.ReleaseGroup, actual.ReleaseGroup);
        Assert.Equal(expected.MediaType, actual.MediaType);
    }
}
