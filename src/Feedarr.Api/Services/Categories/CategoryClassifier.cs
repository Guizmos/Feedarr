using System.Globalization;
using System.Text;

namespace Feedarr.Api.Services.Categories;

/// <summary>
/// Classifie les catégories Torznab en catégories unifiées Feedarr.
/// Entièrement statique et testable sans infrastructure (pas de DB, pas de HTTP).
/// Extrait de CategoryRecommendationService pour permettre les tests unitaires isolés.
/// </summary>
public static class CategoryClassifier
{
    // ─── Constantes de classification ────────────────────────────────────────

    public static readonly HashSet<string> PcTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "pc", "windows", "win32", "win64"
    };

    public static readonly HashSet<string> GameTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "game", "games", "jeu", "jeux", "gaming", "videogame", "videogames"
    };

    public static readonly HashSet<string> AppOsTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "app", "apps", "application", "applications", "software", "softwares", "mobile",
        "android", "ios", "apk", "ipa", "exe", "msi", "dmg", "deb", "rpm", "iso",
        "firmware", "driver", "drivers", "windows", "linux", "macos", "mac"
    };

    public static readonly HashSet<string> BlacklistTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "application", "applications", "app", "apps", "appli", "applis",
        "software", "softwares", "mobile", "apk", "ipa", "exe", "msi", "dmg",
        "deb", "rpm", "iso", "crack", "keygen", "serial", "serials", "warez", "nulled",
        "firmware", "driver", "drivers", "android", "ios", "macos", "mac", "windows", "linux",
        "emulation", "emulator", "emulators", "emu",
        "gps", "garmin", "tomtom",
        "imprimante", "imprimantes", "printer", "printers",
        "console", "xbox", "ps4", "ps5", "playstation", "nintendo", "switch",
        "wallpaper", "wallpapers", "image", "images", "photo", "photos", "pic", "pics", "picture", "pictures",
        "porn", "porno", "erotic", "erotique", "hentai", "nsfw", "xxx", "adult",
        "sport", "sports", "misc", "other", "divers"
    };

    /// <summary>Ordre de priorité pour le rendu des groupes de types.</summary>
    public static readonly string[] UnifiedPriority =
        { "series", "anime", "films", "games", "spectacle", "shows", "audio", "books", "comics", "other" };

    // ─── Tokenisation ─────────────────────────────────────────────────────────

    public static HashSet<string> Tokenize(string? value)
    {
        var normalized = Normalize(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new HashSet<string>(parts, StringComparer.OrdinalIgnoreCase);
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var normalized = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var chars = normalized
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray();
        return new string(chars).Replace("  ", " ").Trim();
    }

    // ─── Classification par ID Torznab ────────────────────────────────────────

    public static string? ClassifyById(int id, HashSet<string> tokens)
    {
        return id switch
        {
            >= 1000 and <= 1999 => "games",
            >= 2000 and <= 2999 => "films",
            >= 3000 and <= 3999 => "audio",
            >= 4000 and <= 4999 => "games",
            >= 5000 and <= 5999 => "series",
            >= 6000 and <= 6999 => "xxx",
            >= 7000 and <= 7999 => "books",
            >= 8000 and <= 8999 => "other",
            _ => null
        };
    }

    // ─── Classification par tokens ────────────────────────────────────────────

    public static string? ClassifyByTokens(HashSet<string> tokens)
    {
        if (tokens.Count == 0) return null;
        var isPcGame = IsPcWindowsGameTokens(tokens);
        if (IsBlacklistedTokens(tokens, isPcGame ? PcTokens : null)) return null;

        var scores = new Dictionary<string, int>
        {
            ["series"] = 0, ["films"] = 0, ["anime"] = 0, ["games"] = 0,
            ["spectacle"] = 0, ["shows"] = 0, ["audio"] = 0, ["books"] = 0, ["comics"] = 0
        };

        var hasSeriesToken = tokens.Overlaps(new[] { "serie", "series", "tv", "tele" });
        var hasAppOsToken  = tokens.Overlaps(AppOsTokens);
        if (hasSeriesToken && !hasAppOsToken) scores["series"] = 3;

        var hasFilmToken = tokens.Overlaps(new[] { "film", "films", "movie", "movies", "cinema" });
        var hasVideoToken = tokens.Contains("video");
        var hasSpectacleToken = tokens.Overlaps(new[]
        {
            "spectacle", "concert", "opera", "theatre", "ballet",
            "symphonie", "orchestr", "philharmon", "ring", "choregraph", "danse"
        });

        if (tokens.Overlaps(new[] { "anime", "animation" })) scores["anime"] = 4;
        if (tokens.Overlaps(new[]
        {
            "audio", "music", "musique", "mp3", "flac", "wav", "aac", "m4a", "opus",
            "podcast", "audiobook", "audiobooks", "album", "albums", "soundtrack", "ost"
        })) scores["audio"] = 4;
        if (tokens.Overlaps(new[]
        {
            "book", "books", "livre", "livres", "ebook", "ebooks", "epub", "mobi", "kindle", "isbn"
        })) scores["books"] = 4;
        if (tokens.Overlaps(new[]
        {
            "comic", "comics", "bd", "manga", "scan", "scans", "graphic", "novel", "novels"
        })) scores["comics"] = 4;
        if (hasSpectacleToken) scores["spectacle"] = 4;
        if (tokens.Overlaps(new[]
        {
            "emission", "show", "talk", "reality", "documentaire", "docu", "magazine",
            "reportage", "enquete", "quotidien", "quotidienne"
        })) scores["shows"] = 4;

        var hasPc   = tokens.Overlaps(PcTokens);
        var hasGame = tokens.Overlaps(new[] { "jeu", "jeux", "game", "games" });
        var isGame  = hasPc && hasGame;
        if (isGame) scores["games"] = 3;

        if (!hasSpectacleToken && (hasFilmToken || (hasVideoToken && !hasGame && !isGame)))
            scores["films"] = 3;

        var maxScore = scores.Values.Max();
        if (maxScore < 3) return null;

        return UnifiedPriority.FirstOrDefault(key => scores.TryGetValue(key, out var score) && score == maxScore);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    public static bool IsPcWindowsGameTokens(HashSet<string> tokens)
    {
        if (tokens.Count == 0) return false;
        var hasPc   = tokens.Overlaps(PcTokens);
        var hasGame = tokens.Overlaps(GameTokens)
            || (tokens.Contains("jeu")  && tokens.Contains("video"))
            || (tokens.Contains("jeux") && tokens.Contains("video"));
        return hasPc && hasGame;
    }

    public static string? FindBlacklistedToken(HashSet<string> tokens, IEnumerable<string>? ignoreTokens = null)
    {
        if (tokens.Count == 0) return null;
        if (ignoreTokens is null)
        {
            foreach (var token in tokens)
                if (BlacklistTokens.Contains(token)) return token;
            return null;
        }
        var ignore = new HashSet<string>(ignoreTokens, StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            if (ignore.Contains(token)) continue;
            if (BlacklistTokens.Contains(token)) return token;
        }
        return null;
    }

    public static bool IsBlacklistedTokens(HashSet<string> tokens, IEnumerable<string>? ignoreTokens = null)
        => !string.IsNullOrWhiteSpace(FindBlacklistedToken(tokens, ignoreTokens));

    public static string LabelForKey(string key) => key switch
    {
        "films"     => "Films",
        "series"    => "Series TV",
        "anime"     => "Anime",
        "games"     => "Jeux PC",
        "spectacle" => "Spectacle",
        "shows"     => "Emissions",
        "audio"     => "Audio",
        "books"     => "Livres",
        "comics"    => "Comics",
        _           => "Autre"
    };
}
