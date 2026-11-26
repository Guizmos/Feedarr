using System.Text.RegularExpressions;
using Feedarr.Api.Models;

namespace Feedarr.Api.Services.Titles;

public sealed partial class TitleParser
{
    private static void SeparateSearchMetadata(ParseInput input, ParsedTitle parsed, TechnicalSuffix suffix)
    {
        if (input.Category == UnifiedCategory.JeuWindows) return;
        if (input.Category == UnifiedCategory.Emission && parsed.AirDate is { Length: >= 4 } broadcast)
            parsed.BroadcastYear = int.Parse(broadcast[..4]);
        // Keep the edition as evidence; only a dated rich release suffix makes a
        // terminal named edition removable from the provider query.
        var edition = Regex.Match(parsed.TitleClean,
            @"(?i)\s+(?:the\s+)?(?:ultimate|special|collector'?s?|anniversary|deluxe)\s+edition$");
        if (edition.Success && suffix.Rich && !RxSeriesPackWords.IsMatch(input.Tokens) && parsed.Year.HasValue && edition.Index > 0 && !Regex.IsMatch(parsed.TitleClean[..edition.Index].Trim(), @"(?i)^(?:the|a|an|le|la|les|un|une)$"))
        {
            parsed.Edition = edition.Value.Trim();
            parsed.TitleClean = parsed.TitleClean[..edition.Index];
        }
        // A repeated uploader identity can accompany a codec/language block
        // before the year. Require the terminal identity as independent proof.
        if (suffix.Rich && parsed.Year.HasValue && parsed.ReleaseGroup is { Length: > 0 } group)
            parsed.TitleClean = Regex.Replace(parsed.TitleClean,
                @"(?i)\s+-\s+(?:divx|xvid)\s+(?:fr|french|en|english)\s+-\s+" + Regex.Escape(group) + "$", "");
        if (parsed.Edition is null && Regex.Match(input.Tokens, @"(?i)\b(?:VFCUT|remastered|restored|directors?\s+cut|uncut)\b") is { Success: true } cut)
            parsed.Edition = cut.Value;
        // An explicit programme/subject separator and broadcast source establish
        // episode scope. A lone year then dates the broadcast, not the programme.
        if (input.Category != UnifiedCategory.Emission || suffix.Source is not ("HDTV" or "TVRip")) return;
        var split = parsed.TitleClean.IndexOf(" - ", StringComparison.Ordinal);
        if (split <= 0 || parsed.TitleClean[(split + 3)..].Trim().Length == 0) return;
        var subject = parsed.TitleClean[(split + 3)..].Trim();
        // A separator alone can be part of the official programme title.
        if (!Regex.IsMatch(subject, @"(?i)^(?:affaire|reportage|sujet|episode|épisode)\b")) return;
        parsed.EpisodeSubject = subject;
        parsed.TitleClean = parsed.TitleClean[..split].Trim();
        parsed.BroadcastYear = parsed.AirDate is { Length: >= 4 } date ? int.Parse(date[..4]) : parsed.Year;
        parsed.Year = null;
    }
}
