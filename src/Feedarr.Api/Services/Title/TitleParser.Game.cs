using System.Text.RegularExpressions;
using Feedarr.Api.Models;

namespace Feedarr.Api.Services.Titles;

public sealed partial class TitleParser
{
    // PC game identity numbers/annual editions are not video release years. This
    // policy owns that distinction and its distribution-aware title normalization.
    private sealed class GameCategoryParser : CategoryParser
    {
        public override string Normalize(string raw, UnifiedCategory category)
            => Regex.Replace(raw, @"\s+", ".").Replace(':', '.').Replace('_', '.');

        public override Match? FindYear(ParseInput input, TechnicalSuffix suffix)
        {
            var raw = input.Tokens;
            var matches = FindYearCandidates(raw);
            if (matches.Count == 0) return null;
            var explicitYear = matches.LastOrDefault(m => IsExplicitYear(raw, m));
            if (explicitYear is not null) return explicitYear;
            var technical = suffix.Start;
            var candidate = matches.LastOrDefault(m => m.Index < technical);
            if (candidate is not null && RxYearRange.Matches(raw).Cast<Match>().Any(range => candidate.Index >= range.Index && candidate.Index < range.Index + range.Length)) return null;
            if (candidate is not null && matches.Count >= 2 && candidate != matches[0]) return candidate;
            // An explicitly delimited metadata region can contain a release date, but a version/build component cannot.
            return matches.FirstOrDefault(m => m.Index >= technical &&
                !new[] { RxGameVersion, RxGameBuild, RxGameVersionNaked, RxGameBuildNum }.Any(rx =>
                    rx.Matches(raw).Cast<Match>().Any(number => m.Index >= number.Index && m.Index < number.Index + number.Length)));
        }

        public override string CleanTitle(ParseInput input, ParsedTitle parsed, TechnicalSuffix suffix, Match? year)
            => FinishTitle(PrepareTitle(input.Tokens, FindTitleBoundaryForYear(input.Tokens, suffix, year)));
    }
}
