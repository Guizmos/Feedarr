namespace Feedarr.Api.Services.Matching;

public static class MatchScorer
{
    public static float ScoreCandidate(
        string queryTitle,
        int? queryYear,
        Feedarr.Api.Models.UnifiedCategory? queryCategory,
        string candidateTitle,
        string? candidateOriginalTitle,
        int? candidateYear,
        string? candidateMediaType)
    {
        var normQuery = TitleNormalizer.NormalizeTitle(queryTitle);
        var normCandidate = TitleNormalizer.NormalizeTitle(candidateTitle);
        var normOriginal = TitleNormalizer.NormalizeTitle(candidateOriginalTitle);

        var titleScore = Math.Max(
            TitleSimilarity(normQuery, normCandidate, queryTitle, candidateTitle),
            TitleSimilarity(normQuery, normOriginal, queryTitle, candidateOriginalTitle)
        );

        var score = titleScore;

        if (queryYear.HasValue && candidateYear.HasValue)
        {
            var diff = Math.Abs(queryYear.Value - candidateYear.Value);
            score += diff switch
            {
                0 => 0.12f,
                1 => 0.05f,
                _ => -0.12f
            };
        }

        var expectedMediaType = queryCategory.HasValue
            ? Feedarr.Api.Services.Categories.UnifiedCategoryMappings.ToMediaType(queryCategory.Value)
            : null;

        if (!string.IsNullOrWhiteSpace(expectedMediaType) &&
            expectedMediaType != "unknown" &&
            !string.IsNullOrWhiteSpace(candidateMediaType))
        {
            score += string.Equals(expectedMediaType, candidateMediaType, StringComparison.OrdinalIgnoreCase)
                ? 0.08f
                : -0.08f;
        }

        var ambiguity = TitleAmbiguityEvaluator.Evaluate(
            normQuery,
            expectedMediaType ?? candidateMediaType,
            queryYear);

        if (ambiguity.IsCommonTitle)
            score -= queryYear.HasValue ? 0.08f : 0.2f;

        if (ambiguity.IsLikelyChannelOrProgram)
            score -= 0.35f;

        if (ambiguity.SignificantTokenCount <= 1)
            score -= 0.05f;

        if (score < 0f) return 0f;
        if (score > 1f) return 1f;
        return score;
    }

    private static float TitleSimilarity(string a, string b, string? rawA, string? rawB)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0f;
        if (string.Equals(a, b, StringComparison.Ordinal)) return 1f;

        var tokensA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokensB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokensA.Length == 0 || tokensB.Length == 0) return 0f;

        var setA = new HashSet<string>(tokensA, StringComparer.Ordinal);
        var setB = new HashSet<string>(tokensB, StringComparer.Ordinal);
        var common = setA.Count(t => setB.Contains(t));

        var score = (2f * common) / (setA.Count + setB.Count);

        if (score < 0.5f)
        {
            var compactA = string.Concat(tokensA);
            var compactB = string.Concat(tokensB);
            if (compactA.Contains(compactB, StringComparison.Ordinal) ||
                compactB.Contains(compactA, StringComparison.Ordinal))
            {
                score = Math.Max(score, 0.75f);
            }
        }

        var significantA = TitleTokenHelper.GetSignificantTokens(a).Count;
        var significantB = TitleTokenHelper.GetSignificantTokens(b).Count;
        if ((score >= 0.35f && score <= 0.65f) || significantA <= 3 || significantB <= 3)
        {
            var compactA = string.Concat(tokensA);
            var compactB = string.Concat(tokensB);
            var fuzzy = JaroWinklerSimilarity(compactA, compactB);
            score = Math.Max(score, fuzzy);
        }

        var splitA = SplitPackedUppercase(a, rawA);
        if (!string.IsNullOrWhiteSpace(splitA))
        {
            score = Math.Max(score, TitleSimilarity(splitA, b, null, null));
        }

        var splitB = SplitPackedUppercase(b, rawB);
        if (!string.IsNullOrWhiteSpace(splitB))
        {
            score = Math.Max(score, TitleSimilarity(a, splitB, null, null));
        }

        return score;
    }

    private static string? SplitPackedUppercase(string normalized, string? raw)
    {
        if (string.IsNullOrWhiteSpace(normalized) || string.IsNullOrWhiteSpace(raw)) return null;
        if (normalized.Contains(' ')) return null;
        var trimmed = raw.Trim();
        if (trimmed.Length < 4 || trimmed.Length > 6) return null;
        if (trimmed.Contains(' ')) return null;
        if (!trimmed.All(char.IsLetter)) return null;
        if (!trimmed.All(char.IsUpper)) return null;
        if (normalized.Length < 4) return null;
        return $"{normalized[..^1]} {normalized[^1]}";
    }

    // Jaro-Winkler similarity [0..1]
    private static float JaroWinklerSimilarity(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2)) return 0f;
        if (string.Equals(s1, s2, StringComparison.Ordinal)) return 1f;

        var len1 = s1.Length;
        var len2 = s2.Length;
        var matchDistance = Math.Max(len1, len2) / 2 - 1;

        var s1Matches = new bool[len1];
        var s2Matches = new bool[len2];

        var matches = 0;
        for (int i = 0; i < len1; i++)
        {
            var start = Math.Max(0, i - matchDistance);
            var end = Math.Min(i + matchDistance + 1, len2);
            for (int j = start; j < end; j++)
            {
                if (s2Matches[j]) continue;
                if (s1[i] != s2[j]) continue;
                s1Matches[i] = true;
                s2Matches[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0) return 0f;

        var t = 0;
        var k = 0;
        for (int i = 0; i < len1; i++)
        {
            if (!s1Matches[i]) continue;
            while (!s2Matches[k]) k++;
            if (s1[i] != s2[k]) t++;
            k++;
        }

        var transpositions = t / 2f;
        var m = matches;
        var jaro = ((m / (float)len1) + (m / (float)len2) + ((m - transpositions) / m)) / 3f;

        var prefix = 0;
        for (int i = 0; i < Math.Min(4, Math.Min(len1, len2)); i++)
        {
            if (s1[i] == s2[i]) prefix++;
            else break;
        }

        const float scaling = 0.1f;
        return jaro + (prefix * scaling * (1f - jaro));
    }
}
