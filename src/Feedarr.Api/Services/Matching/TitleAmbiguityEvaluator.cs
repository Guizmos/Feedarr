namespace Feedarr.Api.Services.Matching;

public sealed record TitleAmbiguityResult(
    bool IsAmbiguous,
    bool IsLikelyChannelOrProgram,
    bool IsCommonTitle,
    int SignificantTokenCount,
    IReadOnlyList<string> Reasons);

public static class TitleAmbiguityEvaluator
{
    private static readonly HashSet<string> CommonTitles = new(StringComparer.Ordinal)
    {
        "ca",
        "red",
        "mama"
    };

    private static readonly HashSet<string> ChannelLikeTokens = new(StringComparer.Ordinal)
    {
        "tf1", "m6", "c8", "w9", "tfx", "gulli", "nrj12", "nrj",
        "france2", "france3", "france4", "france5", "franceinfo",
        "canal", "canalplus", "arte", "bfm", "lci", "rmc"
    };

    public static TitleAmbiguityResult Evaluate(string? normalizedTitle, string? mediaType, int? year)
    {
        var safeNormalized = TitleNormalizer.NormalizeTitle(normalizedTitle);
        var significantTokens = TitleTokenHelper.GetSignificantTokens(safeNormalized);
        var tokenCount = significantTokens.Count;
        var reasons = new List<string>();

        var isSeries = string.Equals(mediaType, "series", StringComparison.OrdinalIgnoreCase);
        var isVeryShort = safeNormalized.Length <= 3;
        var isCommon = tokenCount == 1 && IsCommonTitleToken(significantTokens.FirstOrDefault());
        var isLetterSeparated = LooksLikeLetterSeparated(safeNormalized, significantTokens);
        var yearMissing = !year.HasValue;

        if (isVeryShort) reasons.Add("very-short");
        if (isLetterSeparated) reasons.Add("letters-separated");
        if (isCommon) reasons.Add("common-title");
        if (isSeries && yearMissing && tokenCount < 2) reasons.Add("series-no-year-few-tokens");
        if (tokenCount == 1 && (yearMissing || isCommon)) reasons.Add("single-token");

        var isAmbiguous = isVeryShort
                          || isLetterSeparated
                          || (isSeries && yearMissing && tokenCount < 2)
                          || (tokenCount == 1 && (yearMissing || isCommon));

        return new TitleAmbiguityResult(
            isAmbiguous,
            isLetterSeparated,
            isCommon,
            tokenCount,
            reasons);
    }

    private static bool IsCommonTitleToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (CommonTitles.Contains(token)) return true;
        return token.Length <= 3;
    }

    private static bool LooksLikeLetterSeparated(string normalizedTitle, IReadOnlyList<string> significantTokens)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle)) return false;

        var tokens = normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;

        var compact = string.Concat(tokens);
        if (ChannelLikeTokens.Contains(compact))
            return true;

        if (tokens.Length <= 3 && tokens.All(t => t.Length <= 2))
        {
            if (tokens.Any(t => t.Any(char.IsDigit)))
                return true;
            if (tokens.All(t => t.Length == 1))
                return true;
        }

        if (significantTokens.Count == 0 && tokens.Length <= 2)
            return true;

        if (tokens.Any(t => ChannelLikeTokens.Contains(t)))
            return true;

        return false;
    }
}
