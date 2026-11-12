using Feedarr.Api.Models;
using Feedarr.Api.Services.Categories;

namespace Feedarr.Api.Services.Titles;

public sealed partial class TitleParser
{
    public ParsedTitle Parse(string raw, UnifiedCategory category)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return new ParsedTitle { TitleClean = "" };

        var policy = CategoryParser.For(category);
        var input = new ParseInput(raw, policy.Normalize(raw, category), category);
        var suffix = TechnicalRecognition.Analyze(input.Tokens, input.Original, input.Category);
        var p = new ParsedTitle { ReleaseGroup = suffix.Group };
        policy.ReadStructure(input, p);

        var releaseYear = policy.FindYear(input, suffix);
        if (!p.Year.HasValue)
            p.Year = releaseYear is { } year ? TryInt(year.Value) : null;

        p.Resolution = suffix.Resolution;
        p.Source = suffix.Source;
        p.Codec = suffix.Codec;
        p.MediaType = UnifiedCategoryMappings.ToMediaType(category);

        p.TitleClean = policy.CleanTitle(input, p, suffix, releaseYear);
        if (string.IsNullOrWhiteSpace(p.TitleClean))
            p.TitleClean = FallbackTitle(raw);

        return p;
    }
}
