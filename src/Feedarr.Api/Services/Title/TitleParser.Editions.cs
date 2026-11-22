using System.Text.RegularExpressions;

namespace Feedarr.Api.Services.Titles;

public sealed partial class TitleParser
{
    private sealed record FilmEditionEvidence(Match? Year, int TitleEnd);

    private static readonly Regex RxFilmEdition = new(
        @"(?i)\b(?:remastered|theatrical(?:\s+cut)?|encore\s+edition|\d+(?:st|nd|rd|th)\s+anniversary(?:\s+edition)?)\b",
        RegexOptions.Compiled);

    private static FilmEditionEvidence? FindFilmEditionEvidence(string raw, TechnicalSuffix suffix)
    {
        // Resolution alone, or an edition-like word in free text, is not evidence.
        if (!suffix.Rich || suffix.Start >= raw.Length) return null;
        foreach (Match marker in RxFilmEdition.Matches(raw[..suffix.Start]))
        {
            var prefix = raw[..marker.Index].TrimEnd(' ', '-', '(', '[');
            if (!prefix.Any(char.IsLetter)) continue; // Do not erase a title consisting of the marker.
            var tailStart = marker.Index + marker.Length;
            var tail = raw[tailStart..suffix.Start];
            var datedEdition = Regex.Match(tail, @"^\s*[([]?(?<year>(?:19|20)\d{2})[)\]]?(?=\s|$)");
            var remaining = datedEdition.Success ? tail[datedEdition.Length..] : tail;
            remaining = Regex.Replace(remaining, @"\b(?:AD|MUET)\b", " ");
            if (!IsMetadataWords(remaining)) continue;

            var previous = FindYearCandidates(raw[..marker.Index]).LastOrDefault();
            if (previous is not null &&
                IsMetadataWords(raw[(previous.Index + previous.Length)..marker.Index]))
            {
                // An earlier year followed by a named edition and a later year is
                // lexical evidence for the original date, not provider confirmation.
                if (!datedEdition.Success || TryInt(previous.Value) <= TryInt(datedEdition.Groups["year"].Value))
                    return new(previous, previous.Index);
                // A larger title number cannot be promoted to an original date.
                return new(null, prefix.Length);
            }

            // A marker before the sole year may belong to the work name,
            // even in uppercase. Only a dated named edition is handled here.
            if (datedEdition.Success &&
                Regex.IsMatch(marker.Value, @"(?i)^(?:encore\s+edition|\d+(?:st|nd|rd|th)\s+anniversary)"))
            {
                // Only the edition is dated: do not invent an original release year.
                return new(null, prefix.Length);
            }
        }
        return null;
    }
}
