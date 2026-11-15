using System.Globalization;
using System.Text.RegularExpressions;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Services.Matching;

internal enum CandidateYearEvidence
{
    None,
    Parsed,
    Hypothesis
}

internal enum SearchCandidateOrigin
{
    Primary,
    AmbiguousTrailingNumber
}

/// <summary>Parsed is lexical evidence, never a provider-confirmed release year.</summary>
internal sealed record SearchCandidate(
    string Title,
    int? Year,
    CandidateYearEvidence YearEvidence,
    SearchCandidateOrigin Origin);

/// <summary>
/// The caller supplies identity/manual-lock state from trusted persisted
/// data. IDs themselves are deliberately not query material for this component.
/// </summary>
internal sealed record SearchCandidateInput(
    string? RawTitle,
    UnifiedCategory Category,
    bool HasEstablishedExternalIdentity = false,
    bool HasManualCorrection = false);

/// <summary>
/// Pure, bounded query proposal generation shared by publication and replay.
/// No scoring, identity choice, persistence or IO.
/// </summary>
internal static class SearchCandidateGenerator
{
    internal const int MaxCandidates = 2;
    private static readonly Regex TrailingYearNumber = new(
        @"^(?<title>.+) (?<year>(?:19|20)\d{2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static IReadOnlyList<SearchCandidate> Generate(SearchCandidateInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.HasEstablishedExternalIdentity || input.HasManualCorrection ||
            string.IsNullOrWhiteSpace(input.RawTitle))
            return Array.Empty<SearchCandidate>();

        // Reuse contextual recognition; aggressive matching normalization would
        // silently remove legitimate title numbers or edition names here.
        var parsed = new TitleParser().Parse(input.RawTitle, input.Category);
        if (string.IsNullOrWhiteSpace(parsed.TitleClean)) return Array.Empty<SearchCandidate>();
        var primary = new SearchCandidate(parsed.TitleClean, parsed.Year,
            parsed.Year.HasValue ? CandidateYearEvidence.Parsed : CandidateYearEvidence.None,
            SearchCandidateOrigin.Primary);

        // Movie title-number ambiguity needs resolution evidence. Episodic TV can
        // also propose a season-year hypothesis; other media keep the primary query.
        // A parsed year already separates title number + release year (2049 / 2017).
        if (parsed.Year.HasValue || !(input.Category is UnifiedCategory.Serie or UnifiedCategory.Emission or UnifiedCategory.Anime && parsed.Season.HasValue) &&
            (input.Category != UnifiedCategory.Film || parsed.Resolution is null || parsed.Source is not null || parsed.Codec is not null))
            return Array.AsReadOnly(new[] { primary });

        var number = TrailingYearNumber.Match(parsed.TitleClean);
        var title = number.Groups["title"].Value.Trim();
        if (!number.Success || !title.Any(char.IsLetter))
            return Array.AsReadOnly(new[] { primary });

        var hypothesis = new SearchCandidate(title,
            int.Parse(number.Groups["year"].Value, CultureInfo.InvariantCulture),
            CandidateYearEvidence.Hypothesis, SearchCandidateOrigin.AmbiguousTrailingNumber);
        return Array.AsReadOnly(new[] { primary, hypothesis });
    }
}
