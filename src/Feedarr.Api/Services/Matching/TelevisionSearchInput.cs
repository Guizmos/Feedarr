using System.Globalization;
using System.Text.RegularExpressions;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Services.Matching;

// Search-time interpretation only: persisted titles/categories remain untouched.
internal static class TelevisionSearchInput
{
    internal static ParsedTitle Parse(string raw)
    {
        var parser = new TitleParser();
        var series = parser.Parse(raw, UnifiedCategory.Serie);
        var broadcast = parser.Parse(raw, UnifiedCategory.Emission);
        var parsed = broadcast.AirDate is not null || !series.Season.HasValue ? broadcast : series;
        // A calendar-season label is not an ordinal provider season. With a full,
        // concordant air date, verify the episode by date instead of inventing a mapping.
        if (parsed.Season is >= 1900 and <= 2099 && parsed.AirDate is { Length: >= 4 } date)
        {
            if (int.Parse(date[..4], CultureInfo.InvariantCulture) == parsed.Season)
            {
                parsed.BroadcastYear = parsed.Season;
                parsed.Season = null;
                parsed.Episode = null;
            }
            // Discordant labels remain numbered constraints; they cannot silently disappear.
        }
        return parsed;
    }

    internal static IReadOnlyList<SearchCandidate> Queries(string raw)
    {
        var parsed = Parse(raw);
        if (string.IsNullOrWhiteSpace(parsed.TitleClean)) return Array.Empty<SearchCandidate>();
        var year = parsed.AirDate is null ? parsed.Year : null;
        var explicitOriginalYear = year.HasValue && Regex.IsMatch(raw, @"[\[(]\s*" + year + @"\s*[\])]") && parsed.Source is not ("HDTV" or "TVRip");
        var evidence = year.HasValue ? explicitOriginalYear ? CandidateYearEvidence.Parsed : CandidateYearEvidence.Hypothesis : CandidateYearEvidence.None;
        var title = parsed.EpisodeSubject is null ? parsed.TitleClean : parsed.TitleClean + " - " + parsed.EpisodeSubject;
        var primary = new SearchCandidate(title, year, evidence, SearchCandidateOrigin.Primary);
        var queries = new List<SearchCandidate> { primary };
        var split = title.IndexOf(" - ", StringComparison.Ordinal);
        if (split > 0 && split + 3 < title.Length && (parsed.Season.HasValue || parsed.AirDate is not null || parsed.Source is "HDTV" or "TVRip"))
            queries.Add(new(title[..split].Trim(), null, CandidateYearEvidence.None, SearchCandidateOrigin.ProgrammeTitleHypothesis));
        else if (!year.HasValue && parsed.Season.HasValue && Regex.Match(title, @"^(?<title>.+) (?<year>(?:19|20)\d{2})$") is { Success: true } trailing)
            queries.Add(new(trailing.Groups["title"].Value, int.Parse(trailing.Groups["year"].Value, CultureInfo.InvariantCulture), CandidateYearEvidence.Hypothesis, SearchCandidateOrigin.AmbiguousTrailingNumber));
        else if (Regex.IsMatch(raw, @"(?i)\b(?:integrale|intégrale|complete)\b") && title.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 3 } words)
            // Only a proposal. Acceptance requires this suffix to be an official network
            // of the verified candidate, never a hardcoded list of channel names.
            queries.Add(new(string.Join(' ', words[..^1]), year, evidence, SearchCandidateOrigin.BroadcastSuffixHypothesis, words[^1]));
        return Array.AsReadOnly(queries.ToArray());
    }
}
