using System.Text.RegularExpressions;
using Feedarr.Api.Models;

namespace Feedarr.Api.Services.Titles;

public sealed partial class TitleParser
{
    private static readonly Regex RxContextVideoTech = new(
        @"(?i)\b(?:\d{3,4}[pi]|4k|WEB|H[ .]?26[45]|XVID|DIVX|DTS(?:[ .-]?(?:HDHRA|HD|X|MA))*|E[ .-]?AC3|EAC3|AAC|DDP?\d?|TRUEHD|ATMOS|FLAC|OPUS|MP3|\d{1,2}\s*bits?|[2571][ .][01]|UHD|HDR(?:10\+?)?|DV|DOVI|DOLBY\s+VISION|4KLIGHT|HDLIGHT(?:DD\d)?|3D|SBS|HSBS|REMUX|PROPER|PAL|NTSC|SECAM|MKV|MP4|AVI|MULTIPLE\s+SUBTITLES?)\b", RegexOptions.Compiled);
    private static readonly Regex RxContextSuffix = new(
        @"(?i)(?:[\s._+]+|^)(?<tag>directors?['’]?\s*cut(?:\s*\d+)?|(?:diamond|collector'?s?|ultimate|special|limited|extended|anniversary|deluxe)\s+edition|multi\d*|dual|vff|vfq|vfi|vf\d*|vostfr|vost|vof|truefrench|subfrench|french|english|eng|fr|en|vo|integrale|intégrale|intergale|complete|collection|pack|trilogy|trilogie|saga|anthology|anthologie|coffret|boxset|custom|hybrid|extended|reedition|réédition|repack|restored|remastered|animation|animated|oav|ova)\s*$", RegexOptions.Compiled);
    private static readonly Regex RxContextHardLanguage = new(
        @"(?i)^(?:multi\d*|vff|vfq|vfi|vf\d*|vostfr|vost|vof|truefrench|subfrench)$", RegexOptions.Compiled);
    private static readonly Regex RxContextCertainPack = new(@"(?i)\b(?:integrale|intégrale|intergale|complete)\s*$", RegexOptions.Compiled);

    // Detection and suffix validation use the same source/codec vocabulary.
    private static IEnumerable<Regex> VideoTechnicalPatterns
        => Sources.Select(x => x.rx).Concat(Codecs.Select(x => x.rx)).Append(RxContextVideoTech);

    private static int FindVideoTechnicalStart(string value)
        => VideoTechnicalPatterns.SelectMany(rx => rx.Matches(value).Cast<Match>())
            .Where(m => m.Index > 0 && IsVideoTechnicalTail(value[m.Index..]))
            .Select(m => m.Index).DefaultIfEmpty(value.Length).Min();

    private static bool IsVideoTechnicalTail(string tail)
    {
        tail = Regex.Replace(tail, @"(?i)[-_]\s*[A-Za-z0-9_]{1,30}(?:[ .](?:mkv|mp4|avi))?\s*$", "");
        tail = Regex.Replace(tail, @"(?i)\[[a-f0-9]{8}\]\s*$", "");
        foreach (var rx in VideoTechnicalPatterns) tail = rx.Replace(tail, " ");
        // Numeric audio channels, bit depths and edition numbers are valid only in this suffix.
        tail = Regex.Replace(tail, @"\b\d+\b", " ");
        return IsMetadataWords(tail);
    }

    private static bool HasRichVideoSuffix(string raw)
        => Sources.Any(x => x.rx.IsMatch(raw)) || Codecs.Any(x => x.rx.IsMatch(raw));

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

    private static Match? FindReleaseYear(string raw, UnifiedCategory category)
    {
        var matches = RxYear.Matches(raw).Cast<Match>().Where(m =>
            raw[..m.Index].Trim(' ', '.', '_', '-', '[', '(').Length > 0).ToList();
        if (matches.Count == 0) return null; // A leading number belongs to the title.
        var explicitYear = matches.LastOrDefault(m => IsExplicitYear(raw, m));
        if (explicitYear is not null) return explicitYear;
        var technical = category == UnifiedCategory.JeuWindows ? FindGameMetadataStart(raw) : FindVideoTechnicalStart(raw);
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
                !TryExtractAirDate(raw[(firstSeries.Index + firstSeries.Length)..], out _, out _))
                return afterEpisode;
            technical = Math.Min(technical, firstSeries.Index);
        }
        var candidate = matches.LastOrDefault(m => m.Index < technical);
        // Bare game years/annual editions are not release dates without independent evidence.
        if (category == UnifiedCategory.JeuWindows)
        {
            if (candidate is not null && RxYearRange.Matches(raw).Cast<Match>().Any(range => candidate.Index >= range.Index && candidate.Index < range.Index + range.Length)) return null;
            if (candidate is not null && matches.Count >= 2 && candidate != matches[0]) return candidate;
            // An explicitly delimited metadata region can contain a release date, but a version/build component cannot.
            return matches.FirstOrDefault(m => m.Index >= technical &&
                !new[] { RxGameVersion, RxGameBuild, RxGameVersionNaked, RxGameBuildNum }.Any(rx =>
                    rx.Matches(raw).Cast<Match>().Any(number => m.Index >= number.Index && m.Index < number.Index + number.Length)));
        }
        if (candidate is null) return null;
        if (category is UnifiedCategory.Book or UnifiedCategory.Comic or UnifiedCategory.Audio) return null;
        var tail = raw[(candidate.Index + candidate.Length)..technical].Trim(' ', '.', '_', '-', ')', ']');
        if (tail.Length > 0 && !IsMetadataWords(tail)) return null;
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
            !HasRichVideoSuffix(raw[technical..]) &&
            IsResolutionOnlySuffix(raw[technical..])) return null;
        return candidate;
    }

    private static string CleanContextualTitle(string raw, UnifiedCategory category, ParsedTitle parsed)
    {
        var game = category == UnifiedCategory.JeuWindows;
        var boundary = game ? FindGameMetadataStart(raw) : FindVideoTechnicalStart(raw);
        var year = FindReleaseYear(raw, category);
        if (year is not null)
        {
            var start = year.Index;
            if (IsExplicitYear(raw, year)) start = raw[..start].TrimEnd().Length - 1;
            // Repeated release dates separated only by language tags describe one suffix.
            foreach (Match previous in RxYear.Matches(raw[..year.Index]))
                if (previous.Index > 0 && previous.Value == year.Value && IsMetadataWords(raw[(previous.Index + previous.Length)..year.Index]))
                { start = previous.Index; break; }
            boundary = Math.Min(boundary, start);
        }
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
        var title = raw[..Math.Clamp(boundary, 0, raw.Length)].Trim(' ', '.', '_', '+', '-');
        title = Regex.Replace(title, @"[\[(](?<tags>[^\]\)]+)[\])]\s*$", m => m.Index > 0 && title[..m.Index].Trim(' ', '.', '_', '-').Length > 0 && IsMetadataWords(m.Groups["tags"].Value) ? "" : m.Value);
        title = title.Replace('.', ' ').Replace('_', ' ').Replace('+', ' ');
        if (!game) title = TrimVideoSuffix(title, category, boundary < raw.Length, hasPackRange,
            HasRichVideoSuffix(raw[Math.Min(FindVideoTechnicalStart(raw), raw.Length)..]));
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

    private static int FindGameMetadataStart(string raw)
    {
        var boundary = raw.Length;
        foreach (var rx in GameMetadataPatterns)
            foreach (Match match in rx.Matches(raw))
                if (match.Index > 0 && ("._+-".Contains(raw[match.Index]) || !char.IsLetterOrDigit(raw[match.Index - 1])) &&
                    IsGameTechnicalTail(raw[match.Index..]))
                {
                    var start = match.Index;
                    // "Portal.2.OST" does not prove that 2 counts soundtracks rather than a sequel.
                    if (rx == RxGameBonus || rx == RxGameOst || rx == RxGameDLC)
                    {
                        var number = Regex.Match(match.Value, @"^[._-]?\d+(?=[._-])");
                        if (number.Success) start += number.Length;
                    }
                    boundary = Math.Min(boundary, start);
                }
        foreach (Match version in RxGameVersionNaked.Matches(raw))
        {
            var start = version.Index;
            while (start < raw.Length && "._+-".Contains(raw[start])) start++;
            if (start == 0 || char.IsLetterOrDigit(raw[start - 1])) continue;
            var parts = raw[start..(version.Index + version.Length)].Split('.');
            if (parts.Length == 2 && parts.Any(part => part.Length == 4)) continue;
            if (parts.Length < 3 && !RxGamePlatform.IsMatch(raw[(version.Index + version.Length)..]) &&
                !RxGameScene.IsMatch(raw[(version.Index + version.Length)..])) continue;
            if (!IsGameTechnicalTail(raw[version.Index..])) continue;
            if (parts[0].Length == 4) start += parts[0].Length + 1; // Keep a title's annual edition before a naked version.
            boundary = Math.Min(boundary, start);
        }
        foreach (Match platform in Regex.Matches(raw, @"(?i)(?:[._+\s-])(?:PC|winmac|macwin|linux|macos|windows|x64|x86|win64|win32|amd64|gog|steam|egs|iso|win|mac)(?=[._+\s-]|$)"))
            if (IsGameTechnicalTail(raw[platform.Index..])) boundary = Math.Min(boundary, platform.Index);
        foreach (Match language in RxGameLang.Matches(raw))
        {
            var tag = language.Groups[1].Value;
            if (language.Index > 0 && (boundary < raw.Length || RxGameScene.IsMatch(raw) || RxGameVersionNaked.IsMatch(raw)) &&
                (RxContextHardLanguage.IsMatch(tag) || tag == tag.ToUpperInvariant()) && IsGameTechnicalTail(raw[language.Index..]))
                boundary = Math.Min(boundary, language.Index);
        }
        foreach (Match scene in RxGameScene.Matches(raw))
            if (scene.Index > 0 && (boundary < raw.Length || raw[scene.Index] == '-')) boundary = Math.Min(boundary, scene.Index);
        return boundary;
    }
    private static IEnumerable<Regex> GameMetadataPatterns => new[]
    {
        RxGameDigitalDeluxeEdition, RxGameAnniversaryEdition, RxGameEditionFull, RxGameAllDLC,
        RxGameSupporterPack, RxGameSeasonPass, RxGameDLC, RxGameVersion, RxGameBuild, RxGameSlashBuild,
        RxGameRepack, RxGameEditionShort, RxGameBonus, RxGameOst, RxGameMode, RxGameEarlyAccess, RxGameUpdate
    };

    private static bool IsGameTechnicalTail(string tail)
    {
        tail = RxGameReleaseGroup.Replace(tail, "");
        // Parentheses delimit release metadata too; preserve their contents for validation.
        tail = tail.Replace('(', '.').Replace(')', '.').Replace('[', '.').Replace(']', '.');
        foreach (var rx in GameMetadataPatterns.Concat(new[] { RxGameVersionNaked, RxGameLang, RxGamePlatform, RxGameScene }))
            tail = rx.Replace(tail, ".");
        return tail.All(c => char.IsDigit(c) || char.IsWhiteSpace(c) || "._+-/()[]:".Contains(c));
    }

}
