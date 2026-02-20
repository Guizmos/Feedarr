using System.Globalization;
using System.Text.RegularExpressions;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Categories;

namespace Feedarr.Api.Services.Titles;

public sealed class ParsedTitle
{
    public string TitleClean { get; set; } = "";
    public int? Year { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public string? AirDate { get; set; }
    public string? Resolution { get; set; }
    public string? Source { get; set; }
    public string? Codec { get; set; }
    public string? ReleaseGroup { get; set; }
    public string MediaType { get; set; } = "unknown";
}

public sealed class TitleParser
{
    private static readonly Regex RxSeasonEpisode = new(@"(?i)\bS(?<s>\d{1,2})E(?<e>\d{1,3})(?:E(?<e2>\d{1,3})|-(?<e2>\d{1,3}))?\b", RegexOptions.Compiled);
    private static readonly Regex RxAltSeasonEpisode = new(@"(?i)\b(?<s>\d{1,2})x(?<e>\d{1,3})(?:-(?<e2>\d{1,3}))?\b", RegexOptions.Compiled);
    private static readonly Regex RxSeasonOnly = new(@"(?i)\bS(?<s>\d{1,2})(?!E\d)\b", RegexOptions.Compiled);
    private static readonly Regex RxSeasonWord = new(@"(?i)\b(season|saison)[\s._-]*(?<s>\d{1,2})\b", RegexOptions.Compiled);
    private static readonly Regex RxSeasonDisc = new(@"(?i)\bS(?<s>\d{1,2})D(?<d>\d{1,2})\b", RegexOptions.Compiled);
    private static readonly Regex RxEpisodeOnly = new(@"(?i)\bE(?<e>\d{1,3})\b", RegexOptions.Compiled);
    private static readonly Regex RxAirDateYmd = new(@"\b(?<y>19\d{2}|20\d{2})[._/ -](?<m>\d{1,2})[._/ -](?<d>\d{1,2})\b", RegexOptions.Compiled);
    private static readonly Regex RxAirDateDmy = new(@"\b(?<d>\d{1,2})[._/ -](?<m>\d{1,2})[._/ -](?<y>19\d{2}|20\d{2})\b", RegexOptions.Compiled);
    private static readonly Regex RxSYearEpisode = new(@"(?i)\bS(19\d{2}|20\d{2})E\d{1,3}\b", RegexOptions.Compiled);
    private static readonly Regex RxYear = new(@"\b(19\d{2}|20\d{2})\b", RegexOptions.Compiled);
    private static readonly Regex RxYearRange = new(@"\b(19\d{2}|20\d{2})\s*-\s*(19\d{2}|20\d{2})\b", RegexOptions.Compiled);
    private static readonly Regex RxWeirdRes = new(@"\b\d{3,4}p\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RxRes = new(@"(?i)\b(2160p|1080p|720p|480p|4k)\b", RegexOptions.Compiled);
    private static readonly Regex RxLangTags = new(@"(?i)\b(multi\d*|multi|multisub|multisubs|vff|vfq|vfi|vf|vostfr|vost|vof|truefrench|subfrench|french|fr|eng|english|vo)\b", RegexOptions.Compiled);
    private static readonly Regex RxLangVersionJunk = new(@"(?i)\b(japanese|japonais|english|eng|french|fr)\s+ver(?:sion)?\b", RegexOptions.Compiled);
    private static readonly Regex RxUpperEnTag = new(@"\bEN\b", RegexOptions.Compiled);
    private static readonly Regex RxTrailingWebTag = new(@"(?i)\bWEB\b\s*$", RegexOptions.Compiled);
    private static readonly Regex RxTechSplit = new(@"(?i)\b(2160p|1080p|720p|480p|4k|WEB[- .]?DL|WEB[- .]?RIP|BLURAY|BDRIP|BRRIP|HDTV|DVDRIP|FULL\s*DVD|DVD|x264|x265|HEVC|AV1|MPEG2|AC3|MULTI|VFF|VFQ|VFI|VF|VOSTFR|TRUEFRENCH)\b", RegexOptions.Compiled);
    private static readonly Regex RxGameBrackets = new(@"\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex RxGameParens = new(@"\([^\)]*\)", RegexOptions.Compiled);
    private static readonly Regex RxGameReleaseGroup = new(@"[-_]\s*[A-Za-z0-9_]{2,25}\s*$", RegexOptions.Compiled);
    private static readonly Regex RxGameVersion = new(@"(?i)[._+-]?(v|ver|version)[._-]?\d+(?:[._-]\d+){0,5}[a-z]?", RegexOptions.Compiled);
    private static readonly Regex RxGameVersionNaked = new(@"[._+-]?\d+(?:\.\d+){1,}(?=[._+-]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameBuild = new(@"(?i)[._+-]?(build|rev|revision)[._-]?\d+(?:[._-]\d+)*", RegexOptions.Compiled);
    private static readonly Regex RxGameSlashBuild = new(@"\s*/\s*\d*\s*build\s*\d*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RxGameUpdate = new(@"(?i)[._+-]?(update|hotfix|patch|aio)(?:[._-]?\d+(?:[._-]\d+)*)?", RegexOptions.Compiled);
    private static readonly Regex RxGameYear = new(@"[._+-]?(19\d{2}|20\d{2})(?=[._+-]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameLang = new(@"(?i)(?:^|[._+\-])(multi\d*|multilingual|eng|english|french|fr|german|ger|de|spanish|spa|es|italian|ita|it|russian|rus|ru|japanese|jap|jp|chinese|chn|cn|korean|kor|kr|polish|pol|pl|portuguese|por|pt|brazilian|dutch|dut|nl|swedish|swe|czech|cze|hungarian|hun|turkish|tur|arabic|ara|thai|vietnamese|viet|norwegian|nor|danish|dan|finnish|fin|vf|vff|vfq|vfi|vostfr|vo|truefrench|subfrench|multi-?\d*)(?=[._+\-]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGamePlatform = new(@"(?i)(?:^|[._+\-:])(winmac|macwin|linux|lin|macos|mac|osx|win|windows|x64|x86|win64|win32|amd64|portable|gog|steam|epic|origin|uplay|egs|iso|drm[._-]?free|switch|ps[45]|xbox|nsw)(?=[._+\-:]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameRepack = new(@"(?i)(?:^|[._+\-:])(repack|rip|proper|internal|read[._-]?nfo|nfo[._-]?fix|incl[._-]?dlc|crack[._-]?fix|crack|preactive)(?=[._+\-:]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameScene = new(@"(?i)(?:^|[._+\-:])(fitgirl|elamigos|dodi|rune|skidrow|codex|plaza|flt|tenoke|prophet|cpy|steampunks|darksiders|reloaded|razor1911|hoodlum|fairlight|empress|goldberg|i[_-]?know|notag|noteam|mephisto|k49n|kron4ek|lain|anadius|max\d+|razordox|ali213|3dm|tinyiso|kaos|chronos)(?=[._+\-:]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameAnniversaryEdition = new(@"(?i)[._+-]?\d+[._-]?(year|th|nd|rd|st)?[._-]?anniversary[._-]?edition", RegexOptions.Compiled);
    private static readonly Regex RxGameDigitalDeluxeEdition = new(@"(?i)[._+-]?digital[._+-]?deluxe[._+-]?edition(?=[._+-]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameEditionFull = new(@"(?i)[._+-]?(gold|goty|game[._-]?of[._-]?the[._-]?year|complete|deluxe|ultimate|definitive|collector'?s?|digital|premium|special|enhanced|anniversary|legendary|standard|limited|remastered|remake|hd|4k|super|mega|extreme)[._+-]?edition(?=[._+-]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameEditionShort = new(@"(?i)[._+-]?(remastered|remake|hd[._-]?remaster)(?=[._+-]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameCE = new(@"(?i)[._+-]ce(?=[._+-]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameDLC = new(@"(?i)[._+-]?(\d+[._+-]?)?dlcs?(?=[._+-]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameAllDLC = new(@"(?i)[._+-]?all[._+-]?dlcs?", RegexOptions.Compiled);
    private static readonly Regex RxGameBonus = new(@"(?i)[._+-]?(?:\d+[._+-]?)?bonus[._+-]?(content|pack|wallpapers?|soundtracks?|osts?|artbook)?(?=[._+-]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameOst = new(@"(?i)(?:^|[._+\-:])(?:\d+[._+\-]?)?(?:bonus[._+\-]?)?(osts?|soundtracks?)(?=[._+\-:]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameSupporterPack = new(@"(?i)(?:^|[._+\-:])supporter[._+\-]?pack(?=[._+\-:]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameSeasonPass = new(@"(?i)[._+-]?season[._+-]?pass", RegexOptions.Compiled);
    private static readonly Regex RxGameExpansion = new(@"(?i)[._+-]?expansion[._+-]?(pack)?(?=[._+-]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameBundle = new(@"(?i)[._+-]?(ultimate[._-]?bundle|bundle)(?=[._+-]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameMode = new(@"(?i)(?:^|[._+\-:])(multiplayer|singleplayer|co[._-]?op|coop|lan)(?=[._+\-:]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameEarlyAccess = new(@"(?i)[._+-]?(early[._+-]?access|ea|alpha|beta|demo|trial|preview)(?=[._+-]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameBuildNum = new(@"[._+-]?\d{6,}", RegexOptions.Compiled);
    private static readonly Regex RxGameRevisionTag = new(@"(?i)\br\d{4,}\b", RegexOptions.Compiled);
    private static readonly Regex RxGameHash = new(@"(?i)\b[a-f0-9]{7,}\b", RegexOptions.Compiled);
    private static readonly Regex RxGameTrailingNums = new(@"\s+\d+(\s+\d+)+\s*$", RegexOptions.Compiled);
    private static readonly Regex RxGameTrailingSingleLetter = new(@"\s+\p{L}\s*$", RegexOptions.Compiled);
    private static readonly Regex RxCollectionSuffix = new(@"(?i)\s*[-'"" ]?\s*(integrale|integral|complete|collection|pack|trilogie|trilogy|saga|hexalogie|pentalogie|quadrilogie|quadrilogy|tetralogie|tetralogy|duologie|duology|coffret|boxset|box set|anthology|anthologie)\s*$", RegexOptions.Compiled);
    private static readonly Regex RxCollectionMid = new(@"(?i)\s+[-'"" ]?\s*(integrale|integral|complete|collection|pack|trilogie|trilogy|saga|hexalogie|pentalogie|quadrilogie|quadrilogy|tetralogie|tetralogy|duologie|duology|coffret|boxset|box set|anthology|anthologie)\s+(?=\d{4}|1080p|720p|2160p|480p|WEB|BluRay|HDTV|x264|x265|HEVC|AV1)", RegexOptions.Compiled);
    private static readonly Regex RxSeriesPackWords = new(
        @"(?i)\b(integrale|intégrale|integral|complete|collection|pack|coffret|box\s*set|boxset|anthology|anthologie|saga|trilogie|quadrilogie|tetralogie|duologie|hexalogie|pentalogie|remastered|remaster|repack)\b",
        RegexOptions.Compiled);
    private static readonly Regex RxSeriesJunk = new(
        @"(?i)\b(doc|docu|documentary|documentaire|docuseries|mini[-\s]?series|miniseries)\b",
        RegexOptions.Compiled);
    private static readonly Regex RxAudioJunk = new(
        @"(?i)\b(eac3|aac|ac3|ddp|dd|dts|truehd|atmos|flac|opus|mp3|xvid|divx|5\.1|7\.1|2\.0|1\.0|10bit|10bits|8bit|hdr|dv|dolby\s*vision|4klight|lc)\b",
        RegexOptions.Compiled);
    private static readonly Regex RxVideoStandardJunk = new(@"\b(PAL|NTSC|SECAM)\b", RegexOptions.Compiled);
    private static readonly Regex RxFilmJunk = new(
        @"(?i)\b(repack|custom|re[-._\s]?edition|reedition|réédition)\b",
        RegexOptions.Compiled);
    private static readonly (Regex rx, string value)[] Sources =
    {
        (new Regex(@"(?i)\bWEB[- .]?DL\b", RegexOptions.Compiled), "WEB-DL"),
        (new Regex(@"(?i)\bWEB[- .]?RIP\b", RegexOptions.Compiled), "WEBRip"),
        (new Regex(@"(?i)\bBLURAY\b|\bBDRIP\b|\bBRRIP\b", RegexOptions.Compiled), "BluRay"),
        (new Regex(@"(?i)\bHDTV\b", RegexOptions.Compiled), "HDTV"),
        (new Regex(@"(?i)\bDVDRIP\b", RegexOptions.Compiled), "DVDRip"),
        (new Regex(@"(?i)\bFULL\s*DVD\b", RegexOptions.Compiled), "DVD"),
        (new Regex(@"(?i)\bDVD\b", RegexOptions.Compiled), "DVD"),
    };

    private static readonly (Regex rx, string value)[] Codecs =
    {
        (new Regex(@"(?i)\bx265\b|\bhevc\b|\bh\.?265\b", RegexOptions.Compiled), "x265"),
        (new Regex(@"(?i)\bx264\b|\bavc\b", RegexOptions.Compiled), "x264"),
        (new Regex(@"(?i)\bav1\b", RegexOptions.Compiled), "AV1"),
        (new Regex(@"(?i)\bmpeg-?2\b|\bmpeg2\b", RegexOptions.Compiled), "MPEG2"),
        (new Regex(@"(?i)\bac3\b", RegexOptions.Compiled), "AC3"),
    };

    public ParsedTitle Parse(string raw, UnifiedCategory category)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return new ParsedTitle { TitleClean = "" };

        var p = new ParsedTitle();

        p.ReleaseGroup = ExtractGroup(raw);

        var tokenSource = category == UnifiedCategory.JeuWindows ? raw : PreClean(raw);

        switch (category)
        {
            case UnifiedCategory.Serie:
                ParseSeriesTokens(tokenSource, p);
                break;
            case UnifiedCategory.Emission:
                ParseEmissionTokens(tokenSource, p);
                break;
        }

        if (!p.Year.HasValue)
        {
            var my = RxYear.Match(tokenSource);
            if (my.Success) p.Year = TryInt(my.Value);
        }

        var mr = RxRes.Match(tokenSource);
        if (mr.Success)
            p.Resolution = NormalizeResolution(mr.Value);

        p.Source = Sources.FirstOrDefault(x => x.rx.IsMatch(tokenSource)).value;
        p.Codec = DetectCodec(tokenSource);
        p.MediaType = UnifiedCategoryMappings.ToMediaType(category);

        p.TitleClean = CleanTitle(tokenSource, category, p);
        if (string.IsNullOrWhiteSpace(p.TitleClean))
            p.TitleClean = FallbackTitle(raw);

        return p;
    }

    private static void ParseSeriesTokens(string raw, ParsedTitle p)
    {
        var m = RxSeasonEpisode.Match(raw);
        if (m.Success)
        {
            p.Season = TryInt(m.Groups["s"].Value);
            p.Episode = TryInt(m.Groups["e"].Value);
            return;
        }

        var m2 = RxAltSeasonEpisode.Match(raw);
        if (m2.Success)
        {
            p.Season = TryInt(m2.Groups["s"].Value);
            p.Episode = TryInt(m2.Groups["e"].Value);
            return;
        }

        var md = RxSeasonDisc.Match(raw);
        if (md.Success)
        {
            p.Season = TryInt(md.Groups["s"].Value);
            return;
        }

        var ms = RxSeasonOnly.Match(raw);
        if (ms.Success)
        {
            p.Season = TryInt(ms.Groups["s"].Value);
            return;
        }

        var mw = RxSeasonWord.Match(raw);
        if (mw.Success)
        {
            p.Season = TryInt(mw.Groups["s"].Value);
            return;
        }

        // Episode seul sans saison (ex: E06)
        var me = RxEpisodeOnly.Match(raw);
        if (me.Success)
            p.Episode = TryInt(me.Groups["e"].Value);
    }

    private static void ParseEmissionTokens(string raw, ParsedTitle p)
    {
        if (TryExtractAirDate(raw, out var airDateIso, out var year))
        {
            p.AirDate = airDateIso;
            if (year.HasValue) p.Year = year;
            return;
        }

        ParseSeriesTokens(raw, p);
    }

    private static string CleanTitle(string raw, UnifiedCategory category, ParsedTitle p)
    {
        // Games have their own dedicated cleaning pipeline
        if (category == UnifiedCategory.JeuWindows)
        {
            return CleanGameTitle(raw);
        }

        var s = raw;

        s = Regex.Replace(s, @"\[[^\]]*\]", " ");
        s = Regex.Replace(s, @"\([^\)]*\)", " ");

        switch (category)
        {
            case UnifiedCategory.Serie:
            {
                // Toujours retirer les mots pack/collection (INTEGRALE, COMPLETE, …)
                // avant le split, quel que soit le chemin pris
                s = RxSeriesPackWords.Replace(s, " ");

                var hasSeriesToken =
                    p.Season.HasValue ||
                    p.Episode.HasValue ||
                    RxSeasonEpisode.IsMatch(s) ||
                    RxAltSeasonEpisode.IsMatch(s) ||
                    RxSeasonDisc.IsMatch(s) ||
                    RxSeasonOnly.IsMatch(s) ||
                    RxSeasonWord.IsMatch(s) ||
                    RxEpisodeOnly.IsMatch(s);

                if (hasSeriesToken)
                {
                    s = SplitBeforeSeriesTokens(s);
                }
                else
                {
                    s = SplitBeforeTechTags(s);
                }
                break;
            }

            case UnifiedCategory.Emission:
                s = SplitBeforeEmissionTokens(s);
                break;
            case UnifiedCategory.Film:
            case UnifiedCategory.Animation:
            case UnifiedCategory.Spectacle:
            case UnifiedCategory.Autre:
            default:
                s = SplitBeforeFilmTokens(s, p.Year);
                break;
        }

        s = s.Replace('.', ' ').Replace('_', ' ').Replace('+', ' ');
        s = RxAudioJunk.Replace(s, " ");
        s = RxVideoStandardJunk.Replace(s, " ");
        s = RxCollectionSuffix.Replace(s, "");
        s = RxCollectionMid.Replace(s, " ");
        s = RxYearRange.Replace(s, " ");
        s = RxWeirdRes.Replace(s, " ");
        s = RxCollectionSuffix.Replace(s, "");

        if (category == UnifiedCategory.Serie)
        {
            s = RxAirDateYmd.Replace(s, " ");
            s = RxAirDateDmy.Replace(s, " ");
            s = RxLangVersionJunk.Replace(s, " ");
        }

        if (category == UnifiedCategory.Serie && p.Year.HasValue)
            s = Regex.Replace(s, $@"\b{p.Year}\b", " ");
        if (category == UnifiedCategory.Emission)
        {
            s = RxAirDateYmd.Replace(s, " ");
            s = RxAirDateDmy.Replace(s, " ");
            s = RxSYearEpisode.Replace(s, " ");
            if (p.Year.HasValue)
                s = Regex.Replace(s, $@"\b{p.Year}\b", " ");
        }

        s = RxLangTags.Replace(s, " ");

        if (category == UnifiedCategory.Serie)
        {
            s = RxUpperEnTag.Replace(s, " ");
            s = RxTrailingWebTag.Replace(s, " ");
        }

        if (category == UnifiedCategory.Film)
        {
            s = RxFilmJunk.Replace(s, " ");
            s = RxCollectionSuffix.Replace(s, "");
        }

        if (category == UnifiedCategory.Serie)
            s = RxSeriesJunk.Replace(s, " ");

        s = Regex.Replace(s, @"(?i)\bS\d{1,2}\b", " ");
        s = Regex.Replace(s, @"(?i)\bS(19\d{2}|20\d{2})\b", " ");
        s = Regex.Replace(s, @"(?i)\bS(19\d{2}|20\d{2})E\d{1,3}\b", " ");
        s = Regex.Replace(s, @"(?i)\b(season|saison)\s*\d{1,2}\b", " ");
        s = Regex.Replace(s, @"(?i)\bE\d{1,3}\b", " ");

        while (Regex.IsMatch(s, @"\s*[\(\[][^\)\]]+[\)\]]\s*$"))
            s = Regex.Replace(s, @"\s*[\(\[][^\)\]]+[\)\]]\s*$", "");

        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        s = s.Trim().TrimEnd('-', '.', '(', '[', ' ');

        return s;
    }

    private static string SplitBeforeSeriesTokens(string value)
    {
        if (RxSeasonEpisode.IsMatch(value))
            return Regex.Split(value, @"(?i)\bS\d{1,2}E\d{1,3}(?:E\d{1,3}|-\d{1,3})?\b")[0];
        if (RxAltSeasonEpisode.IsMatch(value))
            return Regex.Split(value, @"(?i)\b\d{1,2}x\d{1,3}(?:-\d{1,3})?\b")[0];
        if (RxSeasonDisc.IsMatch(value))
            return Regex.Split(value, @"(?i)\bS\d{1,2}D\d{1,2}\b")[0];
        if (RxSeasonOnly.IsMatch(value))
            return Regex.Split(value, @"(?i)\bS\d{1,2}\b")[0];
        var mw = RxSeasonWord.Match(value);
        if (mw.Success)
            return value[..mw.Index];
        // Episode seul sans saison (ex: E06)
        var me = RxEpisodeOnly.Match(value);
        if (me.Success)
            return value[..me.Index];
        // Fallback : split avant les tags techniques si aucun token série trouvé
        return SplitBeforeTechTags(value);
    }

    private static string SplitBeforeEmissionTokens(string value)
    {
        if (RxSeasonEpisode.IsMatch(value))
            return Regex.Split(value, @"(?i)\bS\d{1,2}E\d{1,3}(?:E\d{1,3}|-\d{1,3})?\b")[0];
        if (RxAltSeasonEpisode.IsMatch(value))
            return Regex.Split(value, @"(?i)\b\d{1,2}x\d{1,3}(?:-\d{1,3})?\b")[0];
        if (RxAirDateDmy.IsMatch(value))
            return Regex.Split(value, @"\b\d{1,2}[._/ -]\d{1,2}[._/ -](19\d{2}|20\d{2})\b")[0];
        if (RxAirDateYmd.IsMatch(value))
            return Regex.Split(value, @"\b(19\d{2}|20\d{2})[._/ -]\d{1,2}[._/ -]\d{1,2}\b")[0];
        if (RxSYearEpisode.IsMatch(value))
            return Regex.Split(value, @"(?i)\bS(19\d{2}|20\d{2})E\d{1,3}\b")[0];
        return SplitBeforeSeriesTokens(value);
    }

    private static string SplitBeforeFilmTokens(string value, int? year)
    {
        var s = Regex.Split(value, @"(?i)\bS\d{1,2}E\d{1,3}\b")[0];
        if (year.HasValue)
            return Regex.Split(s, @"\b(19\d{2}|20\d{2})\b")[0];
        return SplitBeforeTechTags(s);
    }

    private static string SplitBeforeTechTags(string value)
        => RxTechSplit.Split(value)[0];

    private static string PreClean(string raw)
    {
        var s = raw;
        s = Regex.Replace(s, @"\[[^\]]*\]", " ");
        s = Regex.Replace(s, @"\([^\)]*\)", " ");
        s = s.Replace('.', ' ').Replace('_', ' ').Replace('+', ' ');
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        return s;
    }

    private static string NormalizeResolution(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v == "4k") return "2160p";
        return v;
    }

    private static string? DetectCodec(string raw)
    {
        var hits = new List<string>();
        foreach (var (rx, value) in Codecs)
        {
            if (!rx.IsMatch(raw)) continue;
            if (!hits.Contains(value))
                hits.Add(value);
        }

        if (hits.Count == 0) return null;
        if (hits.Contains("MPEG2") && hits.Contains("AC3"))
            return "MPEG2/AC3";
        return hits[0];
    }

    private static bool TryExtractAirDate(string raw, out string? iso, out int? year)
    {
        iso = null;
        year = null;

        var m = RxAirDateDmy.Match(raw);
        if (!m.Success)
            m = RxAirDateYmd.Match(raw);

        if (!m.Success) return false;

        var y = TryInt(m.Groups["y"].Value);
        var mo = TryInt(m.Groups["m"].Value);
        var d = TryInt(m.Groups["d"].Value);
        if (!y.HasValue || !mo.HasValue || !d.HasValue) return false;

        if (!DateTime.TryParseExact(
                $"{y.Value:D4}-{mo.Value:D2}-{d.Value:D2}",
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dt))
        {
            return false;
        }

        iso = dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        year = dt.Year;
        return true;
    }

    /// <summary>
    /// Comprehensive game title cleaning - applies patterns BEFORE dot conversion
    /// to avoid artifacts like "v2.0.7" becoming "2 0 7"
    /// </summary>
    private static string CleanGameTitle(string raw)
    {
        var s = Regex.Replace(raw, @"\s+", ".").Replace(':', '.');

        // 1. Remove brackets and parentheses content first
        s = RxGameBrackets.Replace(s, " ");
        s = RxGameParens.Replace(s, " ");

        // 2. Remove release group at end (before other processing)
        s = RxGameReleaseGroup.Replace(s, "");

        // 3. Remove version/build patterns BEFORE dot conversion (critical!)
        s = RxGameVersion.Replace(s, ""); // Versions with v prefix: v1.2.3
        s = RxGameBuild.Replace(s, "");
        s = RxGameSlashBuild.Replace(s, ""); // "/ build 123456" patterns
        s = RxGameUpdate.Replace(s, "");

        // 4. Remove years BEFORE naked versions (to avoid "2.2023" being treated as version)
        s = RxGameYear.Replace(s, "");

        // 5. Remove naked version numbers (after year removal to avoid false positives)
        // Sequences like 1.0.41, 2.0.7, 41.78.16 (2+ dot-separated numbers)
        s = RxGameVersionNaked.Replace(s, "");

        // 6. Remove language codes
        s = RxGameLang.Replace(s, ".");

        // 7. Remove platform markers
        s = RxGamePlatform.Replace(s, "");

        // 8. Remove repack/scene group markers
        s = RxGameRepack.Replace(s, "");
        s = RxGameScene.Replace(s, "");

        // 9. Remove edition suffixes (longer/specific patterns first, then general)
        s = RxGameAnniversaryEdition.Replace(s, ""); // "1-Year Anniversary Edition", "10th Anniversary Edition"
        s = RxGameDigitalDeluxeEdition.Replace(s, "");
        s = RxGameEditionFull.Replace(s, "");
        s = RxGameEditionShort.Replace(s, "");
        s = RxGameCE.Replace(s, "");
        s = RxGameBundle.Replace(s, "");
        s = RxGameSupporterPack.Replace(s, "");

        // 10. Remove DLC patterns
        s = RxGameAllDLC.Replace(s, "");
        s = RxGameDLC.Replace(s, "");
        s = RxGameBonus.Replace(s, "");
        s = RxGameOst.Replace(s, "");
        s = RxGameSeasonPass.Replace(s, "");
        s = RxGameExpansion.Replace(s, "");

        // 11. Remove early access / tech markers
        s = RxGameMode.Replace(s, "");
        s = RxGameEarlyAccess.Replace(s, "");

        // 12. Remove long build numbers / revision tags / hash codes
        s = RxGameBuildNum.Replace(s, "");
        s = RxGameRevisionTag.Replace(s, "");
        s = RxGameHash.Replace(s, "");

        // 13. NOW convert delimiters to spaces
        s = s.Replace('.', ' ').Replace('_', ' ').Replace('+', ' ').Replace('-', ' ').Replace(':', ' ');

        // 14. Remove trailing number sequences (version remnants like "1 0 41")
        s = RxGameTrailingNums.Replace(s, "");

        // 15. Remove trailing single-letter tokens (ex: "f")
        s = RxGameTrailingSingleLetter.Replace(s, "");

        // 16. Final cleanup
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        s = s.Trim('-', '.', '(', '[', ')', ']', ' ', '/', '\\', ':');

        return s;
    }

    // Keep old method for backward compatibility but redirect to new one
    private static string CleanGameSuffixes(string value) => value; // No-op, cleaning now done in CleanGameTitle

    private static int? TryInt(string s) => int.TryParse(s, out var n) ? n : null;

    private static string ExtractGroup(string raw)
    {
        var idx = raw.LastIndexOf('-');
        if (idx <= 0 || idx >= raw.Length - 1) return null!;
        var g = raw[(idx + 1)..].Trim();
        if (g.Length < 2 || g.Length > 30) return null!;
        if (g.Any(char.IsWhiteSpace)) return null!;
        return g;
    }

    private static string FallbackTitle(string raw)
    {
        var s = (raw ?? "").Trim();
        s = Regex.Replace(s, @"\[[^\]]*\]", " ");
        s = s.Replace('.', ' ').Replace('_', ' ').Replace('+', ' ');
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        return string.IsNullOrWhiteSpace(s) ? (raw ?? "").Trim() : s;
    }
}
