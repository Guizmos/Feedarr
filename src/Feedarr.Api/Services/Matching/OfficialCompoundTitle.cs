using System.Text.RegularExpressions;

namespace Feedarr.Api.Services.Matching;

internal sealed record CompoundTitleEvidence(OfficialProviderTitle First, OfficialProviderTitle Second);

/// <summary>Exact, ordered coverage by two provider-owned names for one typed TMDB identity. No fuzzy segmentation.</summary>
internal static class OfficialCompoundTitle
{
    internal static IReadOnlyList<OfficialProviderTitle> Verified(ProviderSearchCandidate candidate) =>
        (candidate.OfficialTitles ?? Array.Empty<OfficialProviderTitle>()).Where(t =>
            string.Equals(t.Identity.Provider, candidate.Identity.Provider, StringComparison.OrdinalIgnoreCase) &&
            t.Identity.MediaType == candidate.Identity.MediaType && t.Identity.Id == candidate.Identity.Id &&
            !string.IsNullOrWhiteSpace(t.Title) && SearchCandidateRanker.ValidTitleSource(t)).ToArray();

    internal static CompoundTitleEvidence? Match(string normalized, ProviderSearchCandidate candidate)
    {
        if (!string.Equals(candidate.Identity.Provider, "tmdb", StringComparison.OrdinalIgnoreCase) ||
            candidate.Identity.MediaType is not (SearchMediaType.Movie or SearchMediaType.Series)) return null;
        var titles = Verified(candidate);
        var localized = titles.Where(t => t.Kind == ProviderTitleKind.Localized && !string.IsNullOrWhiteSpace(t.Language))
            .GroupBy(t => TitleNormalizer.NormalizeTitleStrict(t.Title), StringComparer.Ordinal)
            .Where(g => g.Key.Length > 0).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var original in titles.Where(t => t.Kind == ProviderTitleKind.Original))
        {
            var first = TitleNormalizer.NormalizeTitleStrict(original.Title);
            if (first.Length == 0) continue;
            if (normalized.StartsWith(first + " ", StringComparison.Ordinal) && localized.TryGetValue(normalized[(first.Length + 1)..], out var tail) && IndependentNames(first, tail))
                return new(original, tail);
            if (normalized.EndsWith(" " + first, StringComparison.Ordinal) && localized.TryGetValue(normalized[..^(first.Length + 1)], out var head) && IndependentNames(first, head))
                return new(head, original);
        }
        return null;
    }
    private static bool IndependentNames(string original, OfficialProviderTitle localized)
    {
        var other = TitleNormalizer.NormalizeTitleStrict(localized.Title);
        // Identical/contained names cannot justify adding a duplicate title or an arbitrary suffix.
        return !(" " + original + " ").Contains(" " + other + " ", StringComparison.Ordinal) &&
            !(" " + other + " ").Contains(" " + original + " ", StringComparison.Ordinal);
    }
    internal static bool NumericPunctuationAgrees(string query, string official)
    {
        static string[] Significant(string text) => Regex.Matches(text, @"\d+\s*[/,:;+*=×÷]\s*\d+", RegexOptions.CultureInvariant)
            .Select(m => Regex.Replace(m.Value, @"\s+", "")).ToArray();
        // Dot/dash/spacing already normalize identically; do not extend that to fractions, ratios or decimal commas.
        return Significant(query).SequenceEqual(Significant(official));
    }
}
