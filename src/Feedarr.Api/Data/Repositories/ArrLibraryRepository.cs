using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dapper;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Data.Repositories;

public sealed class ArrLibraryRepository
{
    private readonly Db _db;

    public ArrLibraryRepository(Db db)
    {
        _db = db;
    }

    public static string NormalizeTitle(string? title)
    {
#if false
        if (string.IsNullOrWhiteSpace(title)) return "";
        var normalized = title.ToLowerInvariant().Trim();
        // Simple accent removal
        normalized = normalized
            .Replace("é", "e").Replace("è", "e").Replace("ê", "e").Replace("ë", "e")
            .Replace("à", "a").Replace("â", "a").Replace("ä", "a")
            .Replace("ù", "u").Replace("û", "u").Replace("ü", "u")
            .Replace("ô", "o").Replace("ö", "o")
            .Replace("î", "i").Replace("ï", "i")
            .Replace("ç", "c")
            .Replace("ñ", "n")
            .Replace("ß", "ss");
        // Remove non-alphanumeric (keep spaces)
        var sb = new System.Text.StringBuilder();
        foreach (var c in normalized)
        {
            if (char.IsLetterOrDigit(c) || c == ' ')
                sb.Append(c);
        }
        // Collapse multiple spaces and trim
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
#endif
        return TitleNormalizer.NormalizeTitleStrict(title);
    }

    /// <summary>
    /// Sync library items for an app (replaces all existing items)
    /// </summary>
    public void SyncAppLibrary(long appId, string type, List<LibraryItemDto> items)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        try
        {
            // Delete existing items for this app and type
            conn.Execute(
                "DELETE FROM arr_library_items WHERE app_id = @appId AND type = @type",
                new { appId, type },
                tx);

            // Insert new items
            foreach (var item in items)
            {
                var titleNormalized = TitleNormalizer.NormalizeTitleStrict(item.Title);
                var alternateTitlesJson = item.AlternateTitles?.Count > 0
                    ? JsonSerializer.Serialize(item.AlternateTitles)
                    : null;

                conn.Execute(@"
                    INSERT INTO arr_library_items
                    (app_id, type, tmdb_id, tvdb_id, internal_id, title, original_title, title_slug, alternate_titles, title_normalized, synced_at)
                    VALUES
                    (@appId, @type, @tmdbId, @tvdbId, @internalId, @title, @originalTitle, @titleSlug, @alternateTitles, @titleNormalized, datetime('now'))",
                    new
                    {
                        appId,
                        type,
                        tmdbId = item.TmdbId,
                        tvdbId = item.TvdbId,
                        internalId = item.InternalId,
                        title = item.Title,
                        originalTitle = item.OriginalTitle,
                        titleSlug = item.TitleSlug,
                        alternateTitles = alternateTitlesJson,
                        titleNormalized
                    },
                    tx);
            }

            // Update sync status
            conn.Execute(@"
                INSERT INTO arr_sync_status (app_id, last_sync_at, last_sync_count, last_error)
                VALUES (@appId, datetime('now'), @count, NULL)
                ON CONFLICT(app_id) DO UPDATE SET
                    last_sync_at = datetime('now'),
                    last_sync_count = @count,
                    last_error = NULL",
                new { appId, count = items.Count },
                tx);

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Record a sync error for an app
    /// </summary>
    public void SetSyncError(long appId, string error)
    {
        using var conn = _db.Open();
        conn.Execute(@"
            INSERT INTO arr_sync_status (app_id, last_sync_at, last_error)
            VALUES (@appId, datetime('now'), @error)
            ON CONFLICT(app_id) DO UPDATE SET
                last_sync_at = datetime('now'),
                last_error = @error",
            new { appId, error });
    }

    /// <summary>
    /// Record a successful sync for an app with count.
    /// Useful for non-library apps (Overseerr/Jellyseerr requests).
    /// </summary>
    public void SetSyncSuccess(long appId, int count)
    {
        using var conn = _db.Open();
        conn.Execute(@"
            INSERT INTO arr_sync_status (app_id, last_sync_at, last_sync_count, last_error)
            VALUES (@appId, datetime('now'), @count, NULL)
            ON CONFLICT(app_id) DO UPDATE SET
                last_sync_at = datetime('now'),
                last_sync_count = @count,
                last_error = NULL",
            new { appId, count = Math.Max(0, count) });
    }

    /// <summary>
    /// Check if a movie exists in Radarr by tmdbId
    /// </summary>
    public LibraryMatchResult? FindMovieByTmdbId(int tmdbId)
    {
        using var conn = _db.Open();
        return conn.QueryFirstOrDefault<LibraryMatchResult>(@"
            SELECT
                li.internal_id AS InternalId,
                li.tmdb_id AS TmdbId,
                li.title AS Title,
                li.title_slug AS TitleSlug,
                a.base_url AS BaseUrl,
                a.id AS AppId
            FROM arr_library_items li
            JOIN arr_applications a ON a.id = li.app_id
            WHERE li.tmdb_id = @tmdbId
              AND li.type = 'movie'
              AND a.is_enabled = 1
            LIMIT 1",
            new { tmdbId });
    }

    public List<LibraryMatchResult> FindMoviesByTmdbIds(IEnumerable<int> tmdbIds)
    {
        if (tmdbIds is null) return new List<LibraryMatchResult>();
        var ids = tmdbIds.Distinct().ToArray();
        if (ids.Length == 0) return new List<LibraryMatchResult>();

        using var conn = _db.Open();
        return conn.Query<LibraryMatchResult>(@"
            SELECT
                li.internal_id AS InternalId,
                li.tmdb_id AS TmdbId,
                li.title AS Title,
                li.title_slug AS TitleSlug,
                a.base_url AS BaseUrl,
                a.id AS AppId
            FROM arr_library_items li
            JOIN arr_applications a ON a.id = li.app_id
            WHERE li.tmdb_id IN @ids
              AND li.type = 'movie'
              AND a.is_enabled = 1
            ORDER BY li.id",
            new { ids }).AsList();
    }

    /// <summary>
    /// Check if a series exists in Sonarr by tvdbId
    /// </summary>
    public LibraryMatchResult? FindSeriesByTvdbId(int tvdbId)
    {
        using var conn = _db.Open();
        return conn.QueryFirstOrDefault<LibraryMatchResult>(@"
            SELECT
                li.internal_id AS InternalId,
                li.tvdb_id AS TvdbId,
                li.title AS Title,
                li.title_slug AS TitleSlug,
                a.base_url AS BaseUrl,
                a.id AS AppId
            FROM arr_library_items li
            JOIN arr_applications a ON a.id = li.app_id
            WHERE li.tvdb_id = @tvdbId
              AND li.type = 'series'
              AND a.is_enabled = 1
            LIMIT 1",
            new { tvdbId });
    }

    public List<LibraryMatchResult> FindSeriesByTvdbIds(IEnumerable<int> tvdbIds)
    {
        if (tvdbIds is null) return new List<LibraryMatchResult>();
        var ids = tvdbIds.Distinct().ToArray();
        if (ids.Length == 0) return new List<LibraryMatchResult>();

        using var conn = _db.Open();
        return conn.Query<LibraryMatchResult>(@"
            SELECT
                li.internal_id AS InternalId,
                li.tvdb_id AS TvdbId,
                li.title AS Title,
                li.title_slug AS TitleSlug,
                a.base_url AS BaseUrl,
                a.id AS AppId
            FROM arr_library_items li
            JOIN arr_applications a ON a.id = li.app_id
            WHERE li.tvdb_id IN @ids
              AND li.type = 'series'
              AND a.is_enabled = 1
            ORDER BY li.id",
            new { ids }).AsList();
    }

    public List<LibraryTitleMatchResult> FindMoviesByNormalizedTitles(IEnumerable<string> titles)
    {
        if (titles is null) return new List<LibraryTitleMatchResult>();
        var list = titles.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToArray();
        if (list.Length == 0) return new List<LibraryTitleMatchResult>();

        using var conn = _db.Open();
        return conn.Query<LibraryTitleMatchResult>(@"
            SELECT
                li.title_normalized AS TitleNormalized,
                li.title AS Title,
                li.original_title AS OriginalTitle,
                li.tmdb_id AS TmdbId,
                li.internal_id AS InternalId,
                li.title_slug AS TitleSlug,
                a.base_url AS BaseUrl,
                a.id AS AppId
            FROM arr_library_items li
            JOIN arr_applications a ON a.id = li.app_id
            WHERE li.title_normalized IN @list
              AND li.type = 'movie'
              AND a.is_enabled = 1
            ORDER BY li.id",
            new { list }).AsList();
    }

    public List<LibraryTitleMatchResult> FindSeriesByNormalizedTitles(IEnumerable<string> titles)
    {
        if (titles is null) return new List<LibraryTitleMatchResult>();
        var list = titles.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToArray();
        if (list.Length == 0) return new List<LibraryTitleMatchResult>();

        using var conn = _db.Open();
        return conn.Query<LibraryTitleMatchResult>(@"
            SELECT
                li.title_normalized AS TitleNormalized,
                li.title AS Title,
                li.original_title AS OriginalTitle,
                li.tvdb_id AS TvdbId,
                li.internal_id AS InternalId,
                li.title_slug AS TitleSlug,
                a.base_url AS BaseUrl,
                a.id AS AppId
            FROM arr_library_items li
            JOIN arr_applications a ON a.id = li.app_id
            WHERE li.title_normalized IN @list
              AND li.type = 'series'
              AND a.is_enabled = 1
            ORDER BY li.id",
            new { list }).AsList();
    }

    /// <summary>
    /// Find movie by normalized title (fallback)
    /// Uses multiple matching strategies: normalized title, original title, alternate titles
    /// </summary>
    public LibraryMatchResult? FindMovieByTitle(string title)
    {
        var variants = TitleNormalizer.BuildTitleVariants(title);
        if (variants.Length == 0) return null;
        var variantSet = new HashSet<string>(variants);

        using var conn = _db.Open();

        // Layer 1: Try exact normalized title match
        var result = conn.QueryFirstOrDefault<LibraryMatchResult>(@"
            SELECT
                li.internal_id AS InternalId,
                li.tmdb_id AS TmdbId,
                li.title AS Title,
                li.title_slug AS TitleSlug,
                a.base_url AS BaseUrl,
                a.id AS AppId
            FROM arr_library_items li
            JOIN arr_applications a ON a.id = li.app_id
            WHERE li.title_normalized IN @variants
              AND li.type = 'movie'
              AND a.is_enabled = 1
            LIMIT 1",
            new { variants });

        if (result != null) return result;

        // Layer 2: Try original title match (for foreign films)
        result = conn.QueryFirstOrDefault<LibraryMatchResult>(@"
            SELECT
                li.internal_id AS InternalId,
                li.tmdb_id AS TmdbId,
                li.title AS Title,
                li.title_slug AS TitleSlug,
                a.base_url AS BaseUrl,
                a.id AS AppId
            FROM arr_library_items li
            JOIN arr_applications a ON a.id = li.app_id
            WHERE LOWER(li.original_title) = @normalizedTitle
              AND li.type = 'movie'
              AND a.is_enabled = 1
            LIMIT 1",
            new { normalizedTitle = title.ToLowerInvariant().Trim() });

        if (result != null) return result;

        // Layer 3: Try exact case-insensitive title match
        result = conn.QueryFirstOrDefault<LibraryMatchResult>(@"
            SELECT
                li.internal_id AS InternalId,
                li.tmdb_id AS TmdbId,
                li.title AS Title,
                li.title_slug AS TitleSlug,
                a.base_url AS BaseUrl,
                a.id AS AppId
            FROM arr_library_items li
            JOIN arr_applications a ON a.id = li.app_id
            WHERE LOWER(li.title) = @normalizedTitle
              AND li.type = 'movie'
              AND a.is_enabled = 1
            LIMIT 1",
            new { normalizedTitle = title.ToLowerInvariant().Trim() });

        if (result != null) return result;

        // Layer 4: Try alternate titles (search in JSON array)
        var items = conn.Query<(long Id, string? AlternateTitles, int InternalId, int? TmdbId, string Title, string? TitleSlug, string BaseUrl, long AppId)>(@"
            SELECT
                li.id AS Id,
                li.alternate_titles AS AlternateTitles,
                li.internal_id AS InternalId,
                li.tmdb_id AS TmdbId,
                li.title AS Title,
                li.title_slug AS TitleSlug,
                a.base_url AS BaseUrl,
                a.id AS AppId
            FROM arr_library_items li
            JOIN arr_applications a ON a.id = li.app_id
            WHERE li.type = 'movie'
              AND a.is_enabled = 1
              AND li.alternate_titles IS NOT NULL");

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.AlternateTitles)) continue;
            try
            {
                var altTitles = JsonSerializer.Deserialize<List<string>>(item.AlternateTitles);
                if (altTitles is not null)
                {
                    foreach (var alt in altTitles)
                    {
                        var altStrict = TitleNormalizer.NormalizeTitleStrict(alt);
                        if (!string.IsNullOrEmpty(altStrict) && variantSet.Contains(altStrict))
                        {
                            return new LibraryMatchResult
                            {
                                InternalId = item.InternalId,
                                TmdbId = item.TmdbId,
                                Title = item.Title,
                                TitleSlug = item.TitleSlug,
                                BaseUrl = item.BaseUrl,
                                AppId = item.AppId
                            };
                        }

                        var altLoose = TitleNormalizer.NormalizeTitle(alt);
                        if (!string.IsNullOrEmpty(altLoose) && variantSet.Contains(altLoose))
                        {
                            return new LibraryMatchResult
                            {
                                InternalId = item.InternalId,
                                TmdbId = item.TmdbId,
                                Title = item.Title,
                                TitleSlug = item.TitleSlug,
                                BaseUrl = item.BaseUrl,
                                AppId = item.AppId
                            };
                        }
                    }
                }
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// Find series by normalized title (fallback)
    /// Uses multiple matching strategies: normalized title, title slug, alternate titles
    /// </summary>
    public LibraryMatchResult? FindSeriesByTitle(string title)
    {
        var variants = TitleNormalizer.BuildTitleVariants(title);
        if (variants.Length == 0) return null;
        var variantSet = new HashSet<string>(variants);
        var slugVariants = variants
            .Select(v => v.Replace(" ", "-"))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct()
            .ToArray();

        using var conn = _db.Open();

        // Layer 1: Try exact normalized title match
        var result = conn.QueryFirstOrDefault<LibraryMatchResult>(@"
            SELECT
                li.internal_id AS InternalId,
                li.tvdb_id AS TvdbId,
                li.title AS Title,
                li.title_slug AS TitleSlug,
                a.base_url AS BaseUrl,
                a.id AS AppId
            FROM arr_library_items li
            JOIN arr_applications a ON a.id = li.app_id
            WHERE li.title_normalized IN @variants
              AND li.type = 'series'
              AND a.is_enabled = 1
            LIMIT 1",
            new { variants });

        if (result != null) return result;

        // Layer 2: Try title slug match (handles cases like "the-boys", "fallout")
        if (slugVariants.Length > 0)
        {
            result = conn.QueryFirstOrDefault<LibraryMatchResult>(@"
                SELECT
                    li.internal_id AS InternalId,
                    li.tvdb_id AS TvdbId,
                    li.title AS Title,
                    li.title_slug AS TitleSlug,
                    a.base_url AS BaseUrl,
                    a.id AS AppId
                FROM arr_library_items li
                JOIN arr_applications a ON a.id = li.app_id
                WHERE li.title_slug IN @slugVariants
                  AND li.type = 'series'
                  AND a.is_enabled = 1
                LIMIT 1",
                new { slugVariants });
        }

        if (result != null) return result;

        // Layer 3: Try case-insensitive exact title match
        var lowerTitle = title.ToLowerInvariant().Trim();
        result = conn.QueryFirstOrDefault<LibraryMatchResult>(@"
            SELECT
                li.internal_id AS InternalId,
                li.tvdb_id AS TvdbId,
                li.title AS Title,
                li.title_slug AS TitleSlug,
                a.base_url AS BaseUrl,
                a.id AS AppId
            FROM arr_library_items li
            JOIN arr_applications a ON a.id = li.app_id
            WHERE LOWER(li.title) = @lowerTitle
              AND li.type = 'series'
              AND a.is_enabled = 1
            LIMIT 1",
            new { lowerTitle });

        if (result != null) return result;

        // Layer 4: Try alternate titles
        var items = conn.Query<(long Id, string? AlternateTitles, int InternalId, int? TvdbId, string Title, string? TitleSlug, string BaseUrl, long AppId)>(@"
            SELECT
                li.id AS Id,
                li.alternate_titles AS AlternateTitles,
                li.internal_id AS InternalId,
                li.tvdb_id AS TvdbId,
                li.title AS Title,
                li.title_slug AS TitleSlug,
                a.base_url AS BaseUrl,
                a.id AS AppId
            FROM arr_library_items li
            JOIN arr_applications a ON a.id = li.app_id
            WHERE li.type = 'series'
              AND a.is_enabled = 1
              AND li.alternate_titles IS NOT NULL");

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.AlternateTitles)) continue;
            try
            {
                var altTitles = JsonSerializer.Deserialize<List<string>>(item.AlternateTitles);
                if (altTitles is not null)
                {
                    foreach (var alt in altTitles)
                    {
                        var altStrict = TitleNormalizer.NormalizeTitleStrict(alt);
                        if (!string.IsNullOrEmpty(altStrict) && variantSet.Contains(altStrict))
                        {
                            return new LibraryMatchResult
                            {
                                InternalId = item.InternalId,
                                TvdbId = item.TvdbId,
                                Title = item.Title,
                                TitleSlug = item.TitleSlug,
                                BaseUrl = item.BaseUrl,
                                AppId = item.AppId
                            };
                        }

                        var altLoose = TitleNormalizer.NormalizeTitle(alt);
                        if (!string.IsNullOrEmpty(altLoose) && variantSet.Contains(altLoose))
                        {
                            return new LibraryMatchResult
                            {
                                InternalId = item.InternalId,
                                TvdbId = item.TvdbId,
                                Title = item.Title,
                                TitleSlug = item.TitleSlug,
                                BaseUrl = item.BaseUrl,
                                AppId = item.AppId
                            };
                        }
                    }
                }
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// Get sync status for all apps
    /// </summary>
    public List<SyncStatusDto> GetSyncStatus()
    {
        using var conn = _db.Open();
        return conn.Query<SyncStatusDto>(@"
            SELECT
                a.id AS AppId,
                a.name AS AppName,
                a.type AS AppType,
                a.is_enabled AS IsEnabled,
                s.last_sync_at AS LastSyncAt,
                s.last_sync_count AS LastSyncCount,
                s.last_error AS LastError
            FROM arr_applications a
            LEFT JOIN arr_sync_status s ON s.app_id = a.id
            ORDER BY a.type, a.name").AsList();
    }

    /// <summary>
    /// Get last sync time for an app
    /// </summary>
    public DateTime? GetLastSyncTime(long appId)
    {
        using var conn = _db.Open();
        var result = conn.QueryFirstOrDefault<string>(
            "SELECT last_sync_at FROM arr_sync_status WHERE app_id = @appId",
            new { appId });
        return result != null ? DateTime.Parse(result) : null;
    }

    /// <summary>
    /// Get library item count per app
    /// </summary>
    public Dictionary<long, int> GetItemCountByApp()
    {
        using var conn = _db.Open();
        var results = conn.Query<(long AppId, int Count)>(
            "SELECT app_id AS AppId, COUNT(*) AS Count FROM arr_library_items GROUP BY app_id");
        return results.ToDictionary(x => x.AppId, x => x.Count);
    }

    /// <summary>
    /// Delete all library items for an app
    /// </summary>
    public void DeleteAppLibrary(long appId)
    {
        using var conn = _db.Open();
        conn.Execute("DELETE FROM arr_library_items WHERE app_id = @appId", new { appId });
        conn.Execute("DELETE FROM arr_sync_status WHERE app_id = @appId", new { appId });
    }

    /// <summary>
    /// Get total count by type (for debugging)
    /// </summary>
    public int GetTotalCountByType(string type)
    {
        using var conn = _db.Open();
        return conn.QueryFirstOrDefault<int>(
            "SELECT COUNT(*) FROM arr_library_items WHERE type = @type",
            new { type });
    }

    /// <summary>
    /// Get debug items for inspection (for debugging)
    /// </summary>
    public List<DebugLibraryItemDto> GetDebugItems(string? type, string? search, int limit = 20)
    {
        using var conn = _db.Open();

        var sql = @"
            SELECT
                li.id AS Id,
                li.app_id AS AppId,
                li.type AS Type,
                li.tmdb_id AS TmdbId,
                li.tvdb_id AS TvdbId,
                li.internal_id AS InternalId,
                li.title AS Title,
                li.original_title AS OriginalTitle,
                li.title_slug AS TitleSlug,
                li.title_normalized AS TitleNormalized,
                li.alternate_titles AS AlternateTitles,
                a.name AS AppName
            FROM arr_library_items li
            JOIN arr_applications a ON a.id = li.app_id
            WHERE 1=1";

        if (!string.IsNullOrWhiteSpace(type))
        {
            sql += " AND li.type = @type";
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            sql += " AND (LOWER(li.title) LIKE @search OR LOWER(li.title_normalized) LIKE @search OR li.title_slug LIKE @search)";
        }

        sql += " ORDER BY li.title LIMIT @limit";

        var searchPattern = string.IsNullOrWhiteSpace(search) ? null : $"%{search.ToLowerInvariant()}%";

        return conn.Query<DebugLibraryItemDto>(sql, new { type, search = searchPattern, limit }).AsList();
    }
}

public sealed class LibraryItemDto
{
    public int? TmdbId { get; set; }
    public int? TvdbId { get; set; }
    public int InternalId { get; set; }
    public string Title { get; set; } = "";
    public string? OriginalTitle { get; set; }
    public string? TitleSlug { get; set; }
    public List<string>? AlternateTitles { get; set; }
}

public sealed class LibraryMatchResult
{
    public int InternalId { get; set; }
    public int? TmdbId { get; set; }
    public int? TvdbId { get; set; }
    public string Title { get; set; } = "";
    public string? TitleSlug { get; set; }
    public string BaseUrl { get; set; } = "";
    public long AppId { get; set; }
}

public sealed class LibraryTitleMatchResult
{
    public string? TitleNormalized { get; set; }
    public string? Title { get; set; }
    public string? OriginalTitle { get; set; }
    public int? TmdbId { get; set; }
    public int? TvdbId { get; set; }
    public int InternalId { get; set; }
    public string? TitleSlug { get; set; }
    public string BaseUrl { get; set; } = "";
    public long AppId { get; set; }
}

public sealed class SyncStatusDto
{
    public long AppId { get; set; }
    public string? AppName { get; set; }
    public string? AppType { get; set; }
    public bool IsEnabled { get; set; }
    public string? LastSyncAt { get; set; }
    public int LastSyncCount { get; set; }
    public string? LastError { get; set; }
}

public sealed class DebugLibraryItemDto
{
    public long Id { get; set; }
    public long AppId { get; set; }
    public string? AppName { get; set; }
    public string? Type { get; set; }
    public int? TmdbId { get; set; }
    public int? TvdbId { get; set; }
    public int InternalId { get; set; }
    public string? Title { get; set; }
    public string? OriginalTitle { get; set; }
    public string? TitleSlug { get; set; }
    public string? TitleNormalized { get; set; }
    public string? AlternateTitles { get; set; }
}
