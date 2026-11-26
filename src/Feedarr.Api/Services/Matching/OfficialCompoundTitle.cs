using System.Text.RegularExpressions;
using Feedarr.Api.Models;

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

    internal static CompoundTitleEvidence? Match(string normalized, ProviderSearchCandidate candidate, bool omitApostrophes = false, bool allowContained = false)
    {
        if (!string.Equals(candidate.Identity.Provider, "tmdb", StringComparison.OrdinalIgnoreCase) ||
            candidate.Identity.MediaType is not (SearchMediaType.Movie or SearchMediaType.Series)) return null;
        string Normalize(string title) => omitApostrophes ? TitleNormalizer.NormalizeApostropheOmissions(title) : TitleNormalizer.NormalizeTitleStrict(title);
        var titles = Verified(candidate);
        var localized = titles.Where(t => t.Kind == ProviderTitleKind.Localized && !string.IsNullOrWhiteSpace(t.Language))
            .GroupBy(t => Normalize(t.Title), StringComparer.Ordinal)
            .Where(g => g.Key.Length > 0).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var original in titles.Where(t => t.Kind == ProviderTitleKind.Original))
        {
            var first = Normalize(original.Title);
            if (first.Length == 0) continue;
            if (normalized.StartsWith(first + " ", StringComparison.Ordinal) && localized.TryGetValue(normalized[(first.Length + 1)..], out var tail) && Independent(first, Normalize(tail.Title), allowContained))
                return new(original, tail);
            if (normalized.EndsWith(" " + first, StringComparison.Ordinal) && localized.TryGetValue(normalized[..^(first.Length + 1)], out var head) && Independent(first, Normalize(head.Title), allowContained))
                return new(head, original);
        }
        return null;
    }
    private static bool Independent(string first, string second, bool allowContained) => first != second &&
        (allowContained || !(" " + first + " ").Contains(" " + second + " ", StringComparison.Ordinal) &&
            !(" " + second + " ").Contains(" " + first + " ", StringComparison.Ordinal));

    // Canonicalization is video-scoped and used only against provider-owned names.
    // Keep every number: an edition number is still identity-bearing evidence.
    internal static string CanonicalVideo(string title)
    {
        var value = TitleNormalizer.NormalizeTitleStrict(title);
        value = Regex.Replace(value, @"\b0*(\d+)(?:eme|ieme|ere|er|re|e|st|nd|rd|th)\b", "$1");
        value = Regex.Replace(value, @"\bt0*(\d+)\b", "$1");
        value = Regex.Replace(value, @"\b(part|partie|chapitre) 0+(\d+)\b", "$1 $2");
        var romans = new[] { "i", "ii", "iii", "iv", "v", "vi", "vii", "viii", "ix", "x", "xi", "xii", "xiii", "xiv", "xv", "xvi", "xvii", "xviii", "xix", "xx" };
        // Roman numerals at an installment boundary; never convert ordinary words
        // such as MIX, CIVIL or a leading pronoun I into title numbers.
        value = Regex.Replace(value, @"(?<=\s)(?:[ivx]+)(?=$|\s+(?:edition|part|partie|chapitre)\b)", m =>
        {
            var number = Array.IndexOf(romans, m.Value) + 1;
            return number > 0 ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : m.Value;
        });
        return value;
    }

    internal static OfficialProviderTitle? Recognize(SearchCandidate query, CandidateRankingContext context,
        ProviderSearchCandidate candidate, out string? warning)
    {
        warning = null;
        if (candidate.Identity.MediaType is not (SearchMediaType.Movie or SearchMediaType.Series) ||
            context.ExpectedMediaType != candidate.Identity.MediaType || query.YearEvidence != CandidateYearEvidence.Parsed ||
            query.Year is null || query.Year != candidate.Year) return null;
        var canonical = CanonicalVideo(query.Title);
        foreach (var official in Verified(candidate))
        {
            if (!NumericPunctuationAgrees(query.Title, official.Title)) continue;
            var name = CanonicalVideo(official.Title);
            if (canonical == name) { warning = "official-numeric-variant-exact"; return official; }
            // Remove only a terminal, explicit video editorial label after full
            // official coverage. The full official title is always tried first.
            if (canonical.StartsWith(name + " ", StringComparison.Ordinal) &&
                Regex.IsMatch(canonical[(name.Length + 1)..], @"^(?:(?:version|edition) )?(?:restauree?|restored|remastered|longue|extended|director s cut|directors cut)$"))
            { warning = "video-editorial-suffix-warning"; return official; }
            // A first-film label may name the unnumbered original; other numbers
            // never disappear (Gladiator t02 must not become Gladiator).
            if (candidate.Identity.MediaType == SearchMediaType.Movie && !Regex.IsMatch(name, @"\b\d+\b") && canonical == name + " 1")
            { warning = "first-installment-label-warning"; return official; }
            // Event segmentation needs numbered-edition evidence in an official
            // name. Category alone cannot turn a movie's Part 2 into a broadcast.
            if (context.Category == UnifiedCategory.Spectacle && Regex.IsMatch(name, @"\b\d+ edition\b"))
            {
                var editionName = Regex.Replace(name, @"(?<=\d) edition\b", "");
                var eventName = Regex.Replace(canonical, @"(?<=\d) edition\b", "");
                eventName = Regex.Replace(eventName, @" (?:part|partie) \d+$| \d+ soiree$", "");
                if (editionName == eventName)
                { warning = "event-broadcast-segment-warning"; return official; }
            }
        }
        return null;
    }
    internal static bool NumericPunctuationAgrees(string query, string official)
    {
        static string[] Significant(string text) => Regex.Matches(text, @"\d+\s*[/,:;+*=×÷]\s*\d+", RegexOptions.CultureInvariant)
            .Select(m => Regex.Replace(m.Value, @"\s+", "")).ToArray();
        // Dot/dash/spacing already normalize identically; do not extend that to fractions, ratios or decimal commas.
        return Significant(query).SequenceEqual(Significant(official));
    }
}
