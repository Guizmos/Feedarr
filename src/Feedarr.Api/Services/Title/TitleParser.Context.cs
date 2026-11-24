using System.Text.RegularExpressions;
using Feedarr.Api.Models;

namespace Feedarr.Api.Services.Titles;

public sealed partial class TitleParser
{
    private static readonly Regex RxContextSuffix = new(
        @"(?i)(?:[\s._+]+|^)(?<tag>directors?['’]?\s*cut(?:\s*\d+)?|(?:diamond|collector'?s?|ultimate|special|limited|extended|anniversary|deluxe)\s+edition|multi\d*|dual|vff|vfq|vfi|vf\d*|vostfr|vost|vof|truefrench|subfrench|french|english|eng|fr|en|vo|integrale|intégrale|intergale|complete|collection|pack|trilogy|trilogie|saga|anthology|anthologie|coffret|boxset|custom|hybrid|extended|reedition|réédition|repack|restored|remastered|animation|animated|oav|ova)\s*$", RegexOptions.Compiled);
    private static readonly Regex RxContextHardLanguage = new(
        @"(?i)^(?:multi\d*|vff|vfq|vfi|vf\d*|vostfr|vost|vof|truefrench|subfrench)$", RegexOptions.Compiled);
    private static readonly Regex RxContextCertainPack = new(@"(?i)\b(?:integrale|intégrale|intergale|complete)\s*$", RegexOptions.Compiled);

    private static bool IsResolutionOnlySuffix(string suffix)
    {
        suffix = Regex.Replace(suffix, @"(?i)[-_]\s*[A-Za-z0-9_]{1,30}(?:[ .](?:mkv|mp4|avi))?\s*$", "");
        suffix = Regex.Replace(suffix, @"(?i)\b(?:mkv|mp4|avi)\s*$", "");
        return Regex.IsMatch(suffix.Trim(' ', '.', '(', ')', '[', ']'), @"(?i)^(?:\d{3,4}[pi]|4k)$");
    }

    private static bool IsExplicitYear(string raw, Match year)
    {
        var before = raw[..year.Index].TrimEnd();
        var after = raw[(year.Index + year.Length)..].TrimStart();
        return (before.EndsWith('(') && after.StartsWith(')')) || (before.EndsWith('[') && after.StartsWith(']'));
    }

    // Shared lexical year evidence. A category policy decides whether the number
    // dates a work, an edition, a season, or a game distribution.
    private static List<Match> FindYearCandidates(string raw)
        => RxYear.Matches(raw).Cast<Match>().Where(m =>
            raw[..m.Index].Trim(' ', '.', '_', '-', '[', '(').Length > 0).ToList();

    private static Match? FindVideoReleaseYear(string raw, UnifiedCategory category, TechnicalSuffix suffix)
    {
        if (category == UnifiedCategory.Film && FindFilmEditionEvidence(raw, suffix) is { } filmEdition)
            return filmEdition.Year;
        var matches = FindYearCandidates(raw);
        if (matches.Count == 0) return null; // A leading number belongs to the title.
        var explicitYear = matches.LastOrDefault(m => IsExplicitYear(raw, m));
        if (explicitYear is not null) return explicitYear;
        var technical = suffix.Start;
        var videoTechnical = technical;
        var firstSeries = new[] { RxSeasonEpisode, RxAltSeasonEpisode, RxSeasonDisc, RxSeasonOnly, RxSeasonWord, RxEpisodeOnly }
            .Select(rx => rx.Match(raw)).Where(m => m.Success && m.Index > 0).OrderBy(m => m.Index).FirstOrDefault();
        if ((category is UnifiedCategory.Serie or UnifiedCategory.Anime) && firstSeries is not null)
        {
            // A year in an otherwise technical suffix after SxxExx is not part of the show's title.
            var afterEpisode = matches.LastOrDefault(m => m.Index >= firstSeries.Index + firstSeries.Length && m.Index < videoTechnical);
            if (afterEpisode is not null && videoTechnical < raw.Length &&
                IsMetadataWords(raw[(firstSeries.Index + firstSeries.Length)..afterEpisode.Index]) &&
                IsMetadataWords(raw[(afterEpisode.Index + afterEpisode.Length)..videoTechnical]) &&
                !EpisodeRecognition.TryExtractAirDate(raw[(firstSeries.Index + firstSeries.Length)..], out _, out _))
                return afterEpisode;
            technical = Math.Min(technical, firstSeries.Index);
        }
        var candidate = matches.LastOrDefault(m => m.Index < technical);
        if (candidate is null) return null;
        if (category is UnifiedCategory.Book or UnifiedCategory.Comic or UnifiedCategory.Audio) return null;
        var tail = raw[(candidate.Index + candidate.Length)..technical].Trim(' ', '.', '_', '-', ')', ']');
        if (suffix.Rich) tail = Regex.Replace(tail, @"(?:^|[\s._-])AD(?=$|[\s._-])", " ").Trim();
        // Silent-language metadata is only evidence after a candidate year and
        // before an established rich technical suffix. Never strip it from a title.
        if (suffix.Rich)
            tail = Regex.Replace(tail, @"(?:^|[\s._-])MUET(?=$|[\s._-])", " ").Trim(' ', '.', '_', '-');
        if (tail.Length > 0 && !IsMetadataWords(tail) &&
            !(suffix.Rich && IsDatedSubtitleMetadata(tail, category))) return null;
        if ((category is UnifiedCategory.Serie or UnifiedCategory.Anime) && firstSeries is not null &&
            matches.Count == 1 && tail.Length == 0) return null; // A show's own numeric title is ambiguous.
        // A bare trailing number without a format/season/pack signal remains ambiguous.
        var hasSeriesMarker = RxSeasonEpisode.IsMatch(raw) || RxAltSeasonEpisode.IsMatch(raw) || RxSeasonOnly.IsMatch(raw) || RxSeasonWord.IsMatch(raw);
        var hasPack = RxSeriesPackWords.IsMatch(raw[(candidate.Index + candidate.Length)..]);
        if (technical == raw.Length && !hasSeriesMarker && !hasPack) return null;
        // Resolution alone cannot distinguish a title number from a release year.
        var edition = Regex.Match(raw[..candidate.Index], @"[\[(](?<tags>[^\]\)]+)[\])]\s*$");
        var explicitEdition = edition.Success && IsMetadataWords(edition.Groups["tags"].Value);
        if (RxYear.Matches(raw).Count == 1 && tail.Length == 0 && !hasPack && !hasSeriesMarker && !explicitEdition &&
            !suffix.Rich &&
            IsResolutionOnlySuffix(raw[technical..])) return null;
        return candidate;
    }

    private static string CleanContextualTitle(string raw, UnifiedCategory category, ParsedTitle parsed, TechnicalSuffix suffix, Match? year)
    {
        var boundary = FindTitleBoundaryForYear(raw, suffix, year, category);
        var broadcastDate = category == UnifiedCategory.Film ? FindSportsBroadcastDate(raw, suffix) : null;
        // NFL dates terminate the fixture; NBA ISO dates precede the team names.
        if (broadcastDate is not null && !raw.StartsWith("NBA-", StringComparison.OrdinalIgnoreCase))
            boundary = Math.Min(boundary, broadcastDate.Index);
        if (category == UnifiedCategory.Film && FindFilmEditionEvidence(raw, suffix) is { } edition)
            boundary = Math.Min(boundary, edition.TitleEnd);
        if (category is UnifiedCategory.Serie or UnifiedCategory.Anime or UnifiedCategory.Emission)
        {
            foreach (var rx in new[] { RxSeasonEpisode, RxAltSeasonEpisode, RxSeasonDisc, RxSeasonOnly, RxSeasonWord, RxEpisodeOnly, RxSYearEpisode })
            {
                var match = rx.Match(raw); if (match.Success && match.Index > 0) boundary = Math.Min(boundary, match.Index);
            }
            if (category == UnifiedCategory.Emission && parsed.AirDate is not null)
                foreach (var rx in new[] { RxAirDateYmd, RxAirDateDmy })
                { var match = rx.Match(raw); if (match.Success) boundary = Math.Min(boundary, match.Index); }
        }
        var range = RxYearRange.Match(raw);
        var hasPackRange = range.Success && (category is UnifiedCategory.Film or UnifiedCategory.Serie or UnifiedCategory.Animation or UnifiedCategory.Anime) &&
            (RxSeriesPackWords.IsMatch(raw[(range.Index + range.Length)..]) || raw[..range.Index].TrimEnd().EndsWith('('));
        var packContext = hasPackRange || ((category is UnifiedCategory.Serie or UnifiedCategory.Anime) && RxSeriesPackWords.IsMatch(raw));
        if (hasPackRange && range.Index > 0)
        {
            boundary = Math.Min(boundary, raw[..range.Index].TrimEnd().EndsWith('(') ? raw[..range.Index].TrimEnd().Length - 1 : range.Index);
            parsed.Year = null; // A collection range is not one work's release year.
        }
        // Adjacent years in a complete-series pack describe a range, unlike title + release year.
        if (packContext && (category is UnifiedCategory.Serie or UnifiedCategory.Anime))
        {
            var years = RxYear.Matches(raw[..boundary]).Cast<Match>().Where(m => m.Index > 0).ToList();
            if (years.Count > 0 && year is not null && IsMetadataWords(raw[(years[0].Index + years[0].Length)..year.Index]))
            { boundary = years[0].Index; parsed.Year = null; }
        }
        var titleRaw = raw[..Math.Clamp(boundary, 0, raw.Length)];
        if (broadcastDate is not null && broadcastDate.Index + broadcastDate.Length <= boundary)
        {
            var start = broadcastDate.Index > 0 && raw[broadcastDate.Index - 1] == '-' ? broadcastDate.Index - 1 : broadcastDate.Index;
            titleRaw = raw[..start] + " " + raw[(broadcastDate.Index + broadcastDate.Length)..boundary];
        }
        var title = PrepareTitle(titleRaw, titleRaw.Length);
        // A repeated edition flag on both sides of an explicit release year is
        // distribution metadata. A lone significant title word remains untouched.
        if (year is not null && suffix.Rich)
        {
            var flag = Regex.Match(title, @"(?i)\s+(?<tag>extended|remastered|restored|repack)$");
            if (flag.Success && Regex.IsMatch(raw[(year.Index + year.Length)..],
                @"(?i)^[\s._-]*" + Regex.Escape(flag.Groups["tag"].Value) + @"\b"))
                title = title[..flag.Index];
        }
        // A season and an uppercase pack-like word do not distinguish a pack
        // from a genuine title word. Only a pack year range supplies extra context;
        // the existing metadata-chain cleanup remains available in TrimVideoSuffix.
        title = TrimVideoSuffix(title, category, boundary < raw.Length, hasPackRange,
            suffix.Rich);
        return FinishTitle(title);
    }

    private static int FindTitleBoundaryForYear(string raw, TechnicalSuffix suffix, Match? year, UnifiedCategory category = UnifiedCategory.Autre)
    {
        var boundary = suffix.Start;
        if (year is not null)
        {
            var start = year.Index;
            if (IsExplicitYear(raw, year)) start = raw[..start].TrimEnd().Length - 1;
            // Repeated release dates separated only by language tags describe one suffix.
            foreach (Match previous in RxYear.Matches(raw[..year.Index]))
                if (previous.Index > 0 && previous.Value == year.Value &&
                    (IsMetadataWords(raw[(previous.Index + previous.Length)..year.Index]) ||
                     category == UnifiedCategory.Film && suffix.Rich && IsDatedFilmLanguageMetadata(raw[(previous.Index + previous.Length)..year.Index])))
                { start = previous.Index; break; }
            boundary = Math.Min(boundary, start);
        }
        return boundary;
    }

    private static bool IsDatedFilmLanguageMetadata(string value)
        => Regex.IsMatch(value.Trim(), @"(?i)^(?:Tamil|Tamoul)\s+(?:VOSTFR|VOST|SUBFRENCH)$");

    private static bool IsDatedSubtitleMetadata(string value, UnifiedCategory category)
    {
        if (category == UnifiedCategory.Film && IsDatedFilmLanguageMetadata(value)) return true;
        // A language/fansub label is evidence only between a release year and a
        // rich video suffix, with an explicit subtitle declaration.
        return category is UnifiedCategory.Film or UnifiedCategory.Animation &&
            Regex.IsMatch(value.Trim(), @"(?i)^(?:KOREAN\s+VOSTFR|FANSUB\s+VOSTFR|VOSTFR\s+FANSUB)$");
    }

    // A broadcast date is not a generic movie suffix. Recognize only an explicit
    // league/fixture with a validated full date and established broadcast metadata.
    private static Match? FindSportsBroadcastDate(string raw, TechnicalSuffix suffix)
    {
        if (!suffix.Rich) return null;
        if ((suffix.Source is "WEB-DL" or "HDTV") && raw.StartsWith("NBA-", StringComparison.OrdinalIgnoreCase))
        {
            var date = RxAirDateYmd.Match(raw[..suffix.Start]);
            if (date.Success && date.Index == 4 && EpisodeRecognition.TryExtractAirDate(date.Value, out _, out _) &&
                Regex.Match(raw[(date.Index + date.Length)..suffix.Start],
                    @"(?i)^\s+[A-Z0-9]{2,4}\s+VS\s+[A-Z0-9]{2,4}\s+(?<year>(?:19|20)\d{2})\s+(?<metadata>.+)$") is { Success: true } fixture &&
                fixture.Groups["year"].Value == date.Groups["y"].Value && IsMetadataWords(fixture.Groups["metadata"].Value))
                return date;
        }
        if (suffix.Source != "HDTV") return null;
        foreach (Match date in RxAirDateDmy.Matches(raw[..suffix.Start]))
        {
            if (!Regex.IsMatch(raw[..date.Index], @"(?i)^NFL\s+W(?:[1-9]|1\d|2[0-2])\s+.+\s+at\s+.+\s+$") ||
                !IsMetadataWords(raw[(date.Index + date.Length)..suffix.Start]) ||
                !EpisodeRecognition.TryExtractAirDate(date.Value, out _, out _)) continue;
            return date;
        }
        return null;
    }

    private static string PrepareTitle(string raw, int boundary)
    {
        var title = raw[..Math.Clamp(boundary, 0, raw.Length)].Trim(' ', '.', '_', '+', '-');
        title = Regex.Replace(title, @"[\[(](?<tags>[^\]\)]+)[\])]\s*$", m => m.Index > 0 && title[..m.Index].Trim(' ', '.', '_', '-').Length > 0 && IsMetadataWords(m.Groups["tags"].Value) ? "" : m.Value);
        title = title.Replace('.', ' ').Replace('_', ' ').Replace('+', ' ');
        return title;
    }

    private static string FinishTitle(string title)
    {
        title = Regex.Replace(title, @"(?<=\s)-(?=\p{L})", "- ");
        title = Regex.Replace(title, @"(?<=\p{L})-(?=\s)", " -");
        title = Regex.Replace(title, @"\s{2,}", " ").Trim();
        return title.TrimEnd(' ', '-', '.', '(', '[');
    }

    private static bool IsMetadataWords(string value)
    {
        var s = value.Trim(' ', '.', '_', '-', '(', ')', '[', ']');
        if (s.Length == 0) return true;
        while (s.Length > 0)
        {
            var match = RxContextSuffix.Match(s);
            if (!match.Success) return false;
            s = s[..match.Index].Trim(' ', '.', '_', '-');
        }
        return true;
    }

    private static string TrimVideoSuffix(string title, UnifiedCategory category, bool hasBoundary, bool packContext, bool richSuffix)
    {
        var metadata = false;
        while (hasBoundary)
        {
            var match = RxContextSuffix.Match(title);
            if (!match.Success || match.Index == 0) break;
            var tag = match.Groups["tag"].Value;
            var prefix = title[..match.Index];
            var certainPack = RxContextCertainPack.IsMatch(tag);
            var hardLanguage = RxContextHardLanguage.IsMatch(tag);
            var language = Regex.IsMatch(tag, @"(?i)^(french|english|eng|fr|en|vo|dual)$");
            var qualifier = tag.Contains(' ');
            var animation = category is UnifiedCategory.Animation or UnifiedCategory.Anime;
            var uppercaseFlags = Regex.IsMatch(tag, @"^(CUSTOM|HYBRID|EXTENDED|ANIMATION)$") &&
                (metadata || Regex.IsMatch(prefix, @"\b(CUSTOM|HYBRID|EXTENDED|ANIMATION)\s*$"));
            if (!(hardLanguage || (language && (metadata || RxContextCertainPack.IsMatch(prefix) || HasHardLanguageSuffix(prefix) ||
                    (richSuffix && tag == tag.ToUpperInvariant()))) ||
                (certainPack && (metadata || packContext)) || (qualifier && metadata) || uppercaseFlags ||
                (animation && Regex.IsMatch(tag, @"(?i)^(oav|ova)$")))) break;
            metadata = true; title = prefix.TrimEnd();
        }
        return title;
    }

    private static bool HasHardLanguageSuffix(string prefix)
    {
        while (true)
        {
            var match = RxContextSuffix.Match(prefix);
            if (!match.Success) return false;
            if (RxContextHardLanguage.IsMatch(match.Groups["tag"].Value)) return true;
            prefix = prefix[..match.Index];
        }
    }

    private static IEnumerable<Regex> GameMetadataPatterns => new[]
    {
        RxGameDigitalDeluxeEdition, RxGameAnniversaryEdition, RxGameEditionFull, RxGameAllDLC,
        RxGameSupporterPack, RxGameSeasonPass, RxGameDLC, RxGameVersion, RxGameBuild, RxGameSlashBuild,
        RxGameRepack, RxGameEditionShort, RxGameBonus, RxGameOst, RxGameMode, RxGameEarlyAccess, RxGameUpdate
    };

}
