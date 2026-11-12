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

public sealed partial class TitleParser
{
    private static readonly Regex RxSeasonEpisode = new(@"(?i)\bS(?<s>\d{1,2})E(?<e>\d{1,4})(?:v\d+)?(?:E(?<e2>\d{1,4})(?:v\d+)?|-(?<e2>\d{1,4})(?:v\d+)?)?\b", RegexOptions.Compiled);
    private static readonly Regex RxAltSeasonEpisode = new(@"(?i)\b(?<s>\d{1,2})x(?<e>\d{1,4})(?:v\d+)?(?:-(?<e2>\d{1,4})(?:v\d+)?)?\b", RegexOptions.Compiled);
    private static readonly Regex RxSeasonOnly = new(@"(?i)\bS(?<s>\d{1,2})(?!E\d)", RegexOptions.Compiled);
    private static readonly Regex RxSeasonWord = new(@"(?i)\b(season|saison)[\s._-]*(?<s>\d{1,2})\b", RegexOptions.Compiled);
    private static readonly Regex RxSeasonDisc = new(@"(?i)\bS(?<s>\d{1,2})D(?<d>\d{1,2})\b", RegexOptions.Compiled);
    private static readonly Regex RxEpisodeOnly = new(@"(?i)\bE(?<e>\d{1,4})\b", RegexOptions.Compiled);
    private static readonly Regex RxAirDateYmd = new(@"\b(?<y>19\d{2}|20\d{2})[._/ -](?<m>\d{1,2})[._/ -](?<d>\d{1,2})\b", RegexOptions.Compiled);
    private static readonly Regex RxAirDateDmy = new(@"\b(?<d>\d{1,2})[._/ -](?<m>\d{1,2})[._/ -](?<y>19\d{2}|20\d{2})\b", RegexOptions.Compiled);
    private static readonly Regex RxSYearEpisode = new(@"(?i)\bS(19\d{2}|20\d{2})E\d{1,4}\b", RegexOptions.Compiled);
    private static readonly Regex RxYear = new(@"\b(19\d{2}|20\d{2})\b", RegexOptions.Compiled);
    private static readonly Regex RxYearLangGlue = new(
        @"(?i)\b(?<year>19\d{2}|20\d{2})(?<tag>multi\d*|multi|dual|vff|vfq|vfi|vf\d*|vf|vostfr|vost|vof|truefrench|subfrench|french|fr|eng|english|vo|complete|integrale|intégrale|intergale|collection|pack)\b",
        RegexOptions.Compiled);
    private static readonly Regex RxYearRange = new(@"\b(19\d{2}|20\d{2})\s*-\s*(19\d{2}|20\d{2})\b", RegexOptions.Compiled);
    private static readonly Regex RxRes = new(@"(?i)\b(2160p|1080p|1080i|720p|480p|4k)\b", RegexOptions.Compiled);
    private static readonly Regex RxGameReleaseGroup = new(@"[-_]\s*[A-Za-z0-9_]{2,25}\s*$", RegexOptions.Compiled);
    private static readonly Regex RxGameVersion = new(@"(?i)[._+-]?(v|ver|version)[._-]?\d+(?:[._-]\d+){0,5}[a-z]?", RegexOptions.Compiled);
    private static readonly Regex RxGameVersionNaked = new(@"[._+-]?\d+(?:\.\d+){1,}(?=[._+-]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameBuild = new(@"(?i)[._+-]?(build|rev|revision)[._-]?\d+(?:[._-]\d+)*", RegexOptions.Compiled);
    private static readonly Regex RxGameSlashBuild = new(@"\s*/\s*\d*\s*build\s*\d*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RxGameUpdate = new(@"(?i)[._+-]?(update|hotfix|patch|aio)(?:[._-]?\d+(?:[._-]\d+)*)?", RegexOptions.Compiled);
    private static readonly Regex RxGameLang = new(@"(?i)(?:^|[._+\-])(multi\d*|multilingual|eng|english|french|fr|german|ger|de|spanish|spa|es|italian|ita|it|russian|rus|ru|japanese|jap|jp|chinese|chn|cn|korean|kor|kr|polish|pol|pl|portuguese|por|pt|brazilian|dutch|dut|nl|swedish|swe|czech|cze|hungarian|hun|turkish|tur|arabic|ara|thai|vietnamese|viet|norwegian|nor|danish|dan|finnish|fin|vf|vff|vfq|vfi|vostfr|vo|truefrench|subfrench|multi-?\d*)(?=[._+\-]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGamePlatform = new(@"(?i)(?:^|[._+\-:])(pc|winmac|macwin|linux|lin|macos|mac|osx|win|windows|x64|x86|win64|win32|amd64|portable|gog|steam|epic|origin|uplay|egs|iso|drm[._-]?free|switch|ps[45]|xbox|nsw)(?=[._+\-:]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameRepack = new(@"(?i)(?:^|[._+\-:])(repack|rip|proper|internal|read[._-]?nfo|nfo[._-]?fix|incl[._-]?dlc|crack[._-]?fix|crack|preactive)(?=[._+\-:]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameScene = new(@"(?i)(?:^|[._+\-:])(fitgirl|elamigos|dodi|rune|skidrow|codex|plaza|flt|tenoke|prophet|cpy|steampunks|darksiders|reloaded|razor1911|hoodlum|fairlight|empress|goldberg|i[_-]?know|notag|noteam|mephisto|k49n|kron4ek|lain|anadius|max\d+|razordox|ali213|3dm|tinyiso|kaos|chronos)(?=[._+\-:]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameAnniversaryEdition = new(@"(?i)[._+-]?\d+[._-]?(year|th|nd|rd|st)?[._-]?anniversary[._-]?edition", RegexOptions.Compiled);
    private static readonly Regex RxGameDigitalDeluxeEdition = new(@"(?i)[._+-]?digital[._+-]?deluxe[._+-]?edition(?=[._+-]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameEditionFull = new(@"(?i)[._+-]?(gold|goty|game[._-]?of[._-]?the[._-]?year|complete|deluxe|ultimate|definitive|collector'?s?|digital|premium|special|enhanced|anniversary|legendary|standard|limited|remastered|remake|hd|4k|super|mega|extreme)[._+-]?edition(?=[._+-]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameEditionShort = new(@"(?i)[._+-]?(remastered|remake|hd[._-]?remaster)(?=[._+-]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameDLC = new(@"(?i)[._+-]?(\d+[._+-]?)?dlcs?(?=[._+-]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameAllDLC = new(@"(?i)[._+-]?all[._+-]?dlcs?", RegexOptions.Compiled);
    private static readonly Regex RxGameBonus = new(@"(?i)[._+-]?(?:\d+[._+-]?)?bonus[._+-]?(content|pack|wallpapers?|soundtracks?|osts?|artbook)?(?=[._+-]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameOst = new(@"(?i)(?:^|[._+\-:])(?:\d+[._+\-]?)?(?:bonus[._+\-]?)?(osts?|soundtracks?)(?=[._+\-:]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameSupporterPack = new(@"(?i)(?:^|[._+\-:])supporter[._+\-]?pack(?=[._+\-:]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameSeasonPass = new(@"(?i)[._+-]?season[._+-]?pass", RegexOptions.Compiled);
    private static readonly Regex RxGameMode = new(@"(?i)(?:^|[._+\-:])(multiplayer|singleplayer|co[._-]?op|coop|lan)(?=[._+\-:]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameEarlyAccess = new(@"(?i)[._+-]?(early[._+-]?access|ea|alpha|beta|demo|trial|preview)(?=[._+-]|$)", RegexOptions.Compiled);
    private static readonly Regex RxGameBuildNum = new(@"[._+-]?\d{6,}", RegexOptions.Compiled);
    private static readonly Regex RxSeriesPackWords = new(
        @"(?i)\b(integrale|intégrale|intergale|integral|intégral|complete|collection|pack|coffret|box\s*set|boxset|anthology|anthologie|saga|trilogie|quadrilogie|tetralogie|duologie|hexalogie|pentalogie|remastered|remaster|restored|repack)\b",
        RegexOptions.Compiled);
    private static readonly (Regex rx, string value)[] Sources =
    {
        (new Regex(@"(?i)\bWEB[- .]?DL\b", RegexOptions.Compiled), "WEB-DL"),
        (new Regex(@"(?i)\bWEB[- .]?RIP\b", RegexOptions.Compiled), "WEBRip"),
        (new Regex(@"(?i)\bBLU[- ]?RAY\b|\bBDRIP\b|\bBRRIP\b", RegexOptions.Compiled), "BluRay"),
        (new Regex(@"(?i)\bHDTV\b", RegexOptions.Compiled), "HDTV"),
        (new Regex(@"(?i)\bTVRIP\b", RegexOptions.Compiled), "TVRip"),
        (new Regex(@"(?i)\bDVDRIP\b", RegexOptions.Compiled), "DVDRip"),
        (new Regex(@"(?i)\bFULL\s*DVD\b", RegexOptions.Compiled), "DVD"),
        (new Regex(@"(?i)\bDVD\b", RegexOptions.Compiled), "DVD"),
    };

    private static readonly (Regex rx, string value)[] Codecs =
    {
        (new Regex(@"(?i)\bx265\b|\bhevc\b|\bh[ .]?265\b", RegexOptions.Compiled), "x265"),
        (new Regex(@"(?i)\bx264\b|\bavc\b|\bh[ .]?264\b", RegexOptions.Compiled), "x264"),
        (new Regex(@"(?i)\bav1\b", RegexOptions.Compiled), "AV1"),
        (new Regex(@"(?i)\bmpeg-?2\b|\bmpeg2\b", RegexOptions.Compiled), "MPEG2"),
        (new Regex(@"(?i)\bac3\b", RegexOptions.Compiled), "AC3"),
    };

    public ParsedTitle Parse(string raw, UnifiedCategory category)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return new ParsedTitle { TitleClean = "" };

        var p = new ParsedTitle();

        p.ReleaseGroup = ExtractGroup(raw, category);

        var tokenSource = category == UnifiedCategory.JeuWindows ? Regex.Replace(raw, @"\s+", ".").Replace(':', '.').Replace('_', '.') : PreClean(raw, category);

        switch (category)
        {
            case UnifiedCategory.Serie:
            case UnifiedCategory.Anime:
                ParseSeriesTokens(tokenSource, p);
                break;
            case UnifiedCategory.Emission:
                ParseEmissionTokens(tokenSource, p);
                break;
        }

        if (!p.Year.HasValue)
            p.Year = ExtractLikelyYear(tokenSource, category);

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

    private static string CleanTitle(string raw, UnifiedCategory category, ParsedTitle parsed)
        => CleanContextualTitle(raw, category, parsed);

    private static int? ExtractLikelyYear(string raw, UnifiedCategory category)
        => FindReleaseYear(raw, category) is { } year ? TryInt(year.Value) : null;

    private static string PreClean(string raw, UnifiedCategory category)
    {
        var s = raw.Replace('.', ' ').Replace('_', ' ').Replace('+', ' ');
        s = RxYearLangGlue.Replace(s, "${year} ${tag}");
        // Fansub labels describe the release, unlike arbitrary bracketed title words.
        s = Regex.Replace(s, @"^\[(?=[^\]]*(?i:subs|raws))[^\]]+\]\s*", "");
        if (category == UnifiedCategory.Anime &&
            (RxSeasonEpisode.IsMatch(s) || FindVideoTechnicalStart(s) < s.Length))
            s = Regex.Replace(s, @"(?i)^\[fansub\]\s*(?=\S)", "");
        return Regex.Replace(s, @"\s{2,}", " ").Trim();
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

    private static int? TryInt(string s) => int.TryParse(s, out var n) ? n : null;

    private static string ExtractGroup(string raw, UnifiedCategory category)
    {
        var idx = raw.LastIndexOf('-');
        if (idx <= 0 || idx >= raw.Length - 1) return null!;
        var prefix = raw[..idx];
        var suffix = raw[idx..];
        var videoPrefix = Regex.Replace(prefix, @"[._+]", " ");
        var gamePrefix = Regex.Replace(prefix, @"\s+", ".").Replace('_', '.');
        var hasContext = category == UnifiedCategory.JeuWindows
            ? FindGameMetadataStart(gamePrefix) < gamePrefix.Length || RxGameVersion.IsMatch(prefix) ||
              RxGameBuild.IsMatch(prefix) || RxGameScene.IsMatch(suffix)
            : FindVideoTechnicalStart(videoPrefix) < videoPrefix.Length;
        if (!hasContext) return null!;
        var g = raw[(idx + 1)..].Trim();
        if (g.Length < 2 || g.Length > 30) return null!;
        if (!Regex.IsMatch(g, @"^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+){0,2}$")) return null!;
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
