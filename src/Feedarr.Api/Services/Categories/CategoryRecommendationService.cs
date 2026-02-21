using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Dtos.Categories;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Torznab;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Feedarr.Api.Services.Categories;

public sealed class CategoryRecommendationService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private static readonly HashSet<string> RecommendedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "films", "series", "anime", "games", "spectacle", "shows", "audio", "books", "comics"
    };

    private static readonly HashSet<string> PcTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "pc", "windows", "win32", "win64"
    };

    private static readonly HashSet<string> GameTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "game", "games", "jeu", "jeux", "gaming", "videogame", "videogames"
    };

    private static readonly HashSet<string> AppOsTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "app", "apps", "application", "applications", "software", "softwares", "mobile",
        "android", "ios", "apk", "ipa", "exe", "msi", "dmg", "deb", "rpm", "iso",
        "firmware", "driver", "drivers", "windows", "linux", "macos", "mac"
    };

    private static readonly HashSet<string> BlacklistTokens = new(StringComparer.OrdinalIgnoreCase)
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

    private static readonly string[] UnifiedPriority = { "series", "anime", "films", "games", "spectacle", "shows", "audio", "books", "comics", "other" };

    private readonly Db _db;
    private readonly SourceRepository _sources;
    private readonly TorznabClient _torznab;
    private readonly UnifiedCategoryResolver _resolver;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CategoryRecommendationService> _log;

    public CategoryRecommendationService(
        Db db,
        SourceRepository sources,
        TorznabClient torznab,
        UnifiedCategoryResolver resolver,
        IMemoryCache cache,
        ILogger<CategoryRecommendationService> log)
    {
        _db = db;
        _sources = sources;
        _torznab = torznab;
        _resolver = resolver;
        _cache = cache;
        _log = log;
    }

    public void InvalidateSource(long sourceId)
    {
        var key = $"caps:source:{sourceId}";
        _cache.Remove(key);
    }

    public async Task<CapsCategoriesResponseDto> GetDecoratedCapsCategoriesAsync(
        CapsCategoriesRequestDto req,
        CancellationToken ct)
    {
        var warnings = new List<string>();
        var response = new CapsCategoriesResponseDto();

        long? sourceId = req.SourceId;
        string torznabUrl = (req.TorznabUrl ?? "").Trim();
        string apiKey = (req.ApiKey ?? "").Trim();
        string authMode = (req.AuthMode ?? "query").Trim().ToLowerInvariant();
        if (authMode != "header") authMode = "query";
        string indexerName = (req.IndexerName ?? "").Trim();

        List<StoredCategory> storedCats = new();
        Dictionary<int, (string key, string label)> storedUnifiedMap = new();

        if (sourceId.HasValue && sourceId.Value > 0)
        {
            var src = _sources.Get(sourceId.Value);
            if (src is null)
            {
                warnings.Add("Source introuvable.");
                response.Warnings = warnings;
                return response;
            }

            torznabUrl = Convert.ToString(src.TorznabUrl) ?? torznabUrl;
            apiKey = Convert.ToString(src.ApiKey) ?? apiKey;
            authMode = Convert.ToString(src.AuthMode) ?? authMode;
            if (string.IsNullOrWhiteSpace(indexerName))
                indexerName = Convert.ToString(src.Name) ?? "";

            storedCats = LoadStoredCategories(sourceId.Value);
            storedUnifiedMap = storedCats
                .Where(c => !string.IsNullOrWhiteSpace(c.UnifiedKey))
                .ToDictionary(
                    c => c.Id,
                    c => (c.UnifiedKey!, string.IsNullOrWhiteSpace(c.UnifiedLabel) ? LabelForKey(c.UnifiedKey!) : c.UnifiedLabel!));
        }

        if (string.IsNullOrWhiteSpace(torznabUrl))
        {
            warnings.Add("Torznab URL manquante.");
            if (storedCats.Count > 0)
            {
                var storedRaw = storedCats
                    .Select(c => new RawCategory(c.Id, c.Name, c.IsSub, c.ParentId))
                    .ToList();
                response.Categories = DecorateCategories(storedRaw, storedUnifiedMap, indexerName);
            }
            response.Warnings = warnings;
            return response;
        }

        List<RawCategory> rawCaps = await GetCapsCategoriesAsync(
            torznabUrl, authMode, apiKey, sourceId, warnings, ct);

        if (rawCaps.Count == 0)
        {
            if (storedCats.Count > 0)
            {
                if (!warnings.Any(w => w.Contains("Caps", StringComparison.OrdinalIgnoreCase)))
                    warnings.Add("Caps indisponible. Catégories stockées renvoyées.");

                var storedRaw = storedCats
                    .Select(c => new RawCategory(c.Id, c.Name, c.IsSub, c.ParentId))
                    .ToList();
                response.Categories = DecorateCategories(storedRaw, storedUnifiedMap, indexerName);
            }

            response.Warnings = warnings;
            return response;
        }

        var decorated = DecorateCategories(rawCaps, storedUnifiedMap, indexerName);
        response.Categories = decorated;
        response.Warnings = warnings;
        return response;
    }

    private async Task<List<RawCategory>> GetCapsCategoriesAsync(
        string torznabUrl,
        string authMode,
        string apiKey,
        long? sourceId,
        List<string> warnings,
        CancellationToken ct)
    {
        try
        {
            var fingerprint = BuildFingerprint(torznabUrl, authMode, apiKey);
            var cacheKey = BuildCacheKey(sourceId, fingerprint);
            if (_cache.TryGetValue(cacheKey, out CacheEntry? cached) && cached is not null)
            {
                if (!sourceId.HasValue || sourceId.Value <= 0 ||
                    string.Equals(cached.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    return cached.Categories;
                }
            }

            var cats = await _torznab.FetchCapsAsync(torznabUrl, authMode, apiKey, ct);
            var raw = cats.Select(c => new RawCategory(c.id, c.name, c.isSub, c.parentId)).ToList();
            if (raw.Count > 0)
            {
                _cache.Set(cacheKey, new CacheEntry(fingerprint, raw), CacheTtl);
            }
            return raw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Caps fetch failed for {Url}", torznabUrl);
            warnings.Add("Caps indisponible.");
            return new List<RawCategory>();
        }
    }

    private List<CapsCategoryDto> DecorateCategories(
        List<RawCategory> raw,
        Dictionary<int, (string key, string label)> storedMap,
        string? indexerName)
    {
        var result = new List<CapsCategoryDto>();
        var byId = new Dictionary<int, CapsCategoryDto>();
        var tokensById = new Dictionary<int, HashSet<string>>();
        var blacklistTokenById = new Dictionary<int, string?>();

        foreach (var cat in raw)
        {
            var tokens = Tokenize(cat.Name);
            tokensById[cat.Id] = tokens;

            var isPcGame = IsPcWindowsGameTokens(tokens);
            var blacklistToken = FindBlacklistedToken(tokens, isPcGame ? PcTokens : null);
            var isBlacklisted = !string.IsNullOrWhiteSpace(blacklistToken);
            blacklistTokenById[cat.Id] = blacklistToken;

            string? unifiedKey = null;
            string? unifiedLabel = null;
            string? reason = null;

            if (storedMap.TryGetValue(cat.Id, out var stored))
            {
                unifiedKey = stored.key;
                unifiedLabel = stored.label;
                reason = "db";
            }
            else
            {
                var resolvedKey = ResolveByResolver(indexerName, cat.Id);
                if (!string.IsNullOrWhiteSpace(resolvedKey))
                {
                    unifiedKey = resolvedKey;
                    unifiedLabel = LabelForKey(resolvedKey);
                    reason = "resolver";
                }
                else
                {
                    var byIdKey = ClassifyById(cat.Id, tokens);
                    if (!string.IsNullOrWhiteSpace(byIdKey))
                    {
                        unifiedKey = byIdKey;
                        unifiedLabel = LabelForKey(byIdKey);
                        reason = "torznab_range";
                    }
                    else
                    {
                        var byTokensKey = ClassifyByTokens(tokens);
                        if (!string.IsNullOrWhiteSpace(byTokensKey))
                        {
                            unifiedKey = byTokensKey;
                            unifiedLabel = LabelForKey(byTokensKey);
                            reason = $"tokens:{byTokensKey}";
                        }
                    }
                }
            }

            unifiedKey ??= "other";
            unifiedLabel ??= LabelForKey(unifiedKey);

            var dto = new CapsCategoryDto
            {
                Id = cat.Id,
                Name = cat.Name,
                IsSub = cat.IsSub,
                ParentId = cat.ParentId,
                UnifiedKey = unifiedKey,
                UnifiedLabel = unifiedLabel,
                IsRecommended = IsRecommendedKey(unifiedKey, tokens, isBlacklisted),
                Reason = reason
            };

            if (isBlacklisted)
            {
                dto.IsRecommended = false;
                dto.Reason = !string.IsNullOrWhiteSpace(blacklistToken)
                    ? $"blacklist:{blacklistToken}"
                    : "blacklist";
            }

            result.Add(dto);
            byId[cat.Id] = dto;
        }

        foreach (var dto in result.Where(x => x.IsSub && x.ParentId.HasValue))
        {
            if (!byId.TryGetValue(dto.ParentId!.Value, out var parent))
                continue;

            var tokens = tokensById.TryGetValue(dto.Id, out var tk) ? tk : new HashSet<string>();
            var blacklistToken = blacklistTokenById.TryGetValue(dto.Id, out var bl) ? bl : null;
            var isBlacklisted = !string.IsNullOrWhiteSpace(blacklistToken);

            if (isBlacklisted)
            {
                dto.IsRecommended = false;
                dto.Reason = !string.IsNullOrWhiteSpace(blacklistToken)
                    ? $"blacklist:{blacklistToken}"
                    : "blacklist";
                continue;
            }

            if (parent.IsRecommended)
            {
                if (string.IsNullOrWhiteSpace(dto.UnifiedKey) || dto.UnifiedKey == "other")
                {
                    dto.UnifiedKey = parent.UnifiedKey;
                    dto.UnifiedLabel = parent.UnifiedLabel;
                    dto.Reason ??= "inherit-parent";
                }

                dto.IsRecommended = IsRecommendedKey(dto.UnifiedKey, tokens, isBlacklisted);
            }
        }

        return result;
    }

    private string? ResolveByResolver(string? indexerName, int catId)
    {
        int? stdId = catId is >= 1000 and <= 8999 ? catId : null;
        int? specId = catId >= 10000 ? catId : null;

        if (!stdId.HasValue && !specId.HasValue)
            return null;

        var unified = _resolver.Resolve(indexerName, stdId, specId, new[] { catId });
        if (unified == UnifiedCategory.Autre)
            return null;

        return UnifiedCategoryMappings.ToKey(unified);
    }

    private static string LabelForKey(string key)
    {
        return key switch
        {
            "films" => "Films",
            "series" => "Series TV",
            "anime" => "Animation",
            "games" => "Jeux PC",
            "spectacle" => "Spectacle",
            "shows" => "Emissions",
            "audio" => "Audio",
            "books" => "Livres",
            "comics" => "Comics",
            _ => "Autre"
        };
    }

    private static bool IsRecommendedKey(string? unifiedKey, HashSet<string> tokens, bool isBlacklisted = false)
    {
        if (string.IsNullOrWhiteSpace(unifiedKey)) return false;
        if (isBlacklisted) return false;
        if (!RecommendedKeys.Contains(unifiedKey)) return false;

        if (string.Equals(unifiedKey, "games", StringComparison.OrdinalIgnoreCase))
        {
            return IsPcWindowsGameTokens(tokens) && !IsBlacklistedTokens(tokens, PcTokens);
        }

        return true;
    }

    private static bool IsPcWindowsGameTokens(HashSet<string> tokens)
    {
        if (tokens.Count == 0) return false;
        var hasPc = tokens.Overlaps(PcTokens);
        var hasGame = tokens.Overlaps(GameTokens)
            || (tokens.Contains("jeu") && tokens.Contains("video"))
            || (tokens.Contains("jeux") && tokens.Contains("video"));
        return hasPc && hasGame;
    }

    private static string? FindBlacklistedToken(HashSet<string> tokens, IEnumerable<string>? ignoreTokens = null)
    {
        if (tokens.Count == 0) return null;
        if (ignoreTokens is null)
        {
            foreach (var token in tokens)
            {
                if (BlacklistTokens.Contains(token)) return token;
            }
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

    private static bool IsBlacklistedTokens(HashSet<string> tokens, IEnumerable<string>? ignoreTokens = null)
    {
        return !string.IsNullOrWhiteSpace(FindBlacklistedToken(tokens, ignoreTokens));
    }

    private static string? ClassifyByTokens(HashSet<string> tokens)
    {
        if (tokens.Count == 0) return null;
        var isPcGame = IsPcWindowsGameTokens(tokens);
        if (IsBlacklistedTokens(tokens, isPcGame ? PcTokens : null)) return null;

        var scores = new Dictionary<string, int>
        {
            ["series"] = 0,
            ["films"] = 0,
            ["anime"] = 0,
            ["games"] = 0,
            ["spectacle"] = 0,
            ["shows"] = 0,
            ["audio"] = 0,
            ["books"] = 0,
            ["comics"] = 0
        };

        var hasSeriesToken = tokens.Overlaps(new[] { "serie", "series", "tv", "tele" });
        var hasAppOsToken = tokens.Overlaps(AppOsTokens);
        if (hasSeriesToken && !hasAppOsToken) scores["series"] = 3;

        var hasFilmToken = tokens.Overlaps(new[] { "film", "films", "movie", "movies", "cinema" });
        var hasVideoToken = tokens.Contains("video");
        var hasSpectacleToken = tokens.Overlaps(new[]
        {
            "spectacle", "concert", "opera", "theatre", "ballet",
            "symphonie", "orchestr", "philharmon", "ring", "choregraph", "danse"
        });

        if (tokens.Overlaps(new[] { "anime", "animation" })) scores["anime"] = 4;
        if (tokens.Overlaps(new[] { "audio", "music", "musique", "mp3", "flac", "wav", "aac", "m4a", "opus", "podcast", "audiobook", "audiobooks", "album", "albums", "soundtrack", "ost" })) scores["audio"] = 4;
        if (tokens.Overlaps(new[] { "book", "books", "livre", "livres", "ebook", "ebooks", "epub", "mobi", "kindle", "isbn" })) scores["books"] = 4;
        if (tokens.Overlaps(new[] { "comic", "comics", "bd", "manga", "scan", "scans", "graphic", "novel", "novels" })) scores["comics"] = 4;
        if (hasSpectacleToken) scores["spectacle"] = 4;
        if (tokens.Overlaps(new[]
        {
            "emission", "show", "talk", "reality", "documentaire", "docu", "magazine",
            "reportage", "enquete", "quotidien", "quotidienne"
        })) scores["shows"] = 4;

        var hasPc = tokens.Overlaps(PcTokens);
        var hasGame = tokens.Overlaps(new[] { "jeu", "jeux", "game", "games" });
        var isGame = hasPc && hasGame;
        if (isGame) scores["games"] = 3;

        if (!hasSpectacleToken && (hasFilmToken || (hasVideoToken && !hasGame && !isGame)))
            scores["films"] = 3;

        var maxScore = scores.Values.Max();
        if (maxScore < 3) return null;

        return UnifiedPriority.FirstOrDefault(key => scores.TryGetValue(key, out var score) && score == maxScore);
    }

    private static string? ClassifyById(int id, HashSet<string> tokens)
    {
        if (id == 5070) return "anime";
        if (id >= 3000 && id < 4000) return "audio";
        if (id >= 2000 && id < 3000) return "films";
        if (id >= 5000 && id < 6000) return "series";
        if (id == 4050) return "games";
        if (id >= 7000 && id < 8000)
        {
            if (id >= 7030 && id < 7040) return "comics";
            return "books";
        }
        if (id >= 4000 && id < 5000)
        {
            if (tokens.Overlaps(new[] { "jeu", "jeux", "game", "games", "pc", "windows" }))
                return "games";
        }
        return null;
    }

    private static HashSet<string> Tokenize(string? value)
    {
        var normalized = Normalize(value);
        if (string.IsNullOrWhiteSpace(normalized)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new HashSet<string>(parts, StringComparer.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var normalized = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var chars = normalized
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray();
        return new string(chars).Replace("  ", " ").Trim();
    }

    private List<StoredCategory> LoadStoredCategories(long sourceId)
    {
        using var conn = _db.Open();
        var rows = conn.Query<StoredCategory>(
            """
            SELECT
              cat_id as Id,
              name as Name,
              parent_cat_id as ParentId,
              is_sub as IsSub,
              unified_key as UnifiedKey,
              unified_label as UnifiedLabel
            FROM source_categories
            WHERE source_id = @sid
            ORDER BY cat_id ASC;
            """,
            new { sid = sourceId }
        );
        return rows.ToList();
    }

    private static string BuildFingerprint(string torznabUrl, string authMode, string apiKey)
    {
        var raw = $"{torznabUrl}|{authMode}|{apiKey}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string BuildCacheKey(long? sourceId, string fingerprint)
    {
        if (sourceId.HasValue && sourceId.Value > 0)
            return $"caps:source:{sourceId.Value}";
        return $"caps:torznab:{fingerprint}";
    }

    private sealed record CacheEntry(string Fingerprint, List<RawCategory> Categories);

    private sealed record RawCategory(int Id, string Name, bool IsSub, int? ParentId);

    private sealed class StoredCategory
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int? ParentId { get; set; }
        public bool IsSub { get; set; }
        public string? UnifiedKey { get; set; }
        public string? UnifiedLabel { get; set; }
    }
}
