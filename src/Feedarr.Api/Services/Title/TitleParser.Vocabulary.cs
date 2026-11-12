using System.Text.RegularExpressions;
using Feedarr.Api.Models;

namespace Feedarr.Api.Services.Titles;

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
    private static readonly Regex RxGameRepack = new(@"(?i)(?:^|[._+\-:])(repack|rip|proper|internal|read[._-]?nfo|nfo[._-]?fix|incl[._-]?dlc|crack[._-]?fix|crack|preactive[d]?)(?=[._+\-:]|$)", RegexOptions.Compiled);
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
        (new Regex(@"(?i)\bBLU[- ]?RAY(?:[- ]?RIP)?\b|\bBDRIP\b|\bBRRIP\b", RegexOptions.Compiled), "BluRay"),
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

}
