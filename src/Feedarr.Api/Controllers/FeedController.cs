using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Categories;
using Microsoft.Extensions.Caching.Memory;
using System.Text.RegularExpressions;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/feed")]
public sealed class FeedController : ControllerBase
{
    private readonly Db _db;
    private readonly UnifiedCategoryService _unified;
    private readonly IMemoryCache _cache;
    private const string RawRatingExpr = "COALESCE(me.ext_rating, releases.ext_rating)";
    private const string NormalizedRatingExpr = "(CASE WHEN " + RawRatingExpr + " > 10 THEN " + RawRatingExpr + " / 10.0 ELSE " + RawRatingExpr + " END)";
    private static readonly TimeSpan TopCacheDuration = TimeSpan.FromSeconds(20);
    private static readonly Regex SearchTokenRegex = new(@"[a-zA-Z0-9_-]+", RegexOptions.Compiled);
    private const int MaxFtsTokenLength = 64;

    public FeedController(Db db, UnifiedCategoryService unified, IMemoryCache cache)
    {
        _db = db;
        _unified = unified;
        _cache = cache;
    }

    private sealed class FeedRow
    {
        public long Id { get; set; }
        public long SourceId { get; set; }
        public string? Title { get; set; }
        public string? TitleClean { get; set; }
        public int? Year { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public string? Resolution { get; set; }
        public string? Source { get; set; }
        public string? Codec { get; set; }
        public string? ReleaseGroup { get; set; }
        public string? MediaType { get; set; }
        public int? Seeders { get; set; }
        public int? Leechers { get; set; }
        public int? Grabs { get; set; }
        public long? SizeBytes { get; set; }
        public long? PublishedAt { get; set; }
        public int? CategoryId { get; set; }
        public string? CategoryName { get; set; }
        public int? Seen { get; set; }
        public string? DownloadPath { get; set; }
        public string? PosterUrl { get; set; }
        public long? PosterUpdatedAtTs { get; set; }
        public long? EntityId { get; set; }
        public string? PosterProvider { get; set; }
        public string? PosterLang { get; set; }
        public string? PosterSize { get; set; }
        public string? PosterLastError { get; set; }
        public long? PosterLastAttemptTs { get; set; }
        public string? Overview { get; set; }
        public string? Tagline { get; set; }
        public string? Genres { get; set; }
        public string? ReleaseDate { get; set; }
        public long? RuntimeMinutes { get; set; }
        public double? Rating { get; set; }
        public long? RatingVotes { get; set; }
        public string? DetailsProvider { get; set; }
        public string? DetailsProviderId { get; set; }
        public long? DetailsUpdatedAtTs { get; set; }
        public string? Directors { get; set; }
        public string? Writers { get; set; }
        public string? Cast { get; set; }
        public int? StdCategoryId { get; set; }
        public int? SpecCategoryId { get; set; }
        public string? CategoryIds { get; set; }
        public string? UnifiedCategory { get; set; }
        public string? UnifiedCategoryKey { get; set; }
        public string? UnifiedCategoryLabel { get; set; }
        // IDs for Sonarr/Radarr integration
        public long? TmdbId { get; set; }
        public long? TvdbId { get; set; }
        public bool? IsInSonarr { get; set; }
        public bool? IsInRadarr { get; set; }
        public string? SonarrUrl { get; set; }
        public string? RadarrUrl { get; set; }
        public string? OpenUrl { get; set; }
        public long? ArrCheckedAtTs { get; set; }
    }

    private static string NormalizeTopSort(string? sortBy)
    {
        var normalized = (sortBy ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "seeders" => "seeders",
            "rating" => "rating",
            "downloads" => "downloads",
            "recent" => "recent",
            "date" => "recent",
            _ => "seeders"
        };
    }

    private static string GetTopSortWhereClause(string topSort)
    {
        return topSort switch
        {
            "rating" => NormalizedRatingExpr + " IS NOT NULL AND " + NormalizedRatingExpr + " > 0",
            "downloads" => "grabs IS NOT NULL AND grabs > 0",
            "recent" => "1=1",
            _ => "seeders IS NOT NULL AND seeders > 0"
        };
    }

    private static string GetTopSortOrderClause(string topSort)
    {
        return topSort switch
        {
            "rating" => NormalizedRatingExpr + " DESC, COALESCE(me.ext_votes, releases.ext_votes, 0) DESC, seeders DESC, published_at_ts DESC",
            "downloads" => "grabs DESC, seeders DESC, published_at_ts DESC",
            "recent" => "published_at_ts DESC, seeders DESC",
            _ => "seeders DESC, published_at_ts DESC"
        };
    }

    private static string? BuildFtsQuery(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var tokens = SearchTokenRegex
            .Matches(input.ToLowerInvariant())
            .Select(m => m.Value.Trim())
            .Where(t => t.Length >= 2 && t.Length <= MaxFtsTokenLength)
            .Distinct()
            .Take(8)
            .ToList();

        if (tokens.Count == 0)
            return null;

        return string.Join(" AND ", tokens.Select(t => $"\"{EscapeFtsToken(t)}\"*"));
    }

    private static string EscapeFtsToken(string token)
        => token.Replace("\"", "\"\"", StringComparison.Ordinal);

    // GET /api/feed/{sourceId}?limit=50&q=foo&categoryId=102154&minSeeders=5&seen=0
    [HttpGet("{sourceId:long}")]
    public IActionResult Latest(
        [FromRoute] long sourceId,
        [FromQuery] int? limit,
        [FromQuery] string? q,
        [FromQuery] int? categoryId,
        [FromQuery] int? minSeeders,
        [FromQuery] int? seen,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var lim = Math.Clamp(limit ?? 100, 1, 500);

        using var conn = _db.Open();
        var hasCategories = conn.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='source_categories';"
        ) > 0;
        var hasFts = conn.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='releases_fts';"
        ) > 0;

        var where = new List<string> { "releases.source_id = @sid" };
        var args = new DynamicParameters();
        args.Add("sid", sourceId);
        args.Add("lim", lim);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var trimmedQuery = q.Trim();
            if (hasFts)
            {
                var ftsQuery = BuildFtsQuery(trimmedQuery);
                if (!string.IsNullOrWhiteSpace(ftsQuery))
                {
                    where.Add("releases.id IN (SELECT rowid FROM releases_fts WHERE releases_fts MATCH @qfts)");
                    args.Add("qfts", ftsQuery);
                }
                else
                {
                    where.Add("(title LIKE @q OR releases.title_clean LIKE @q)");
                    args.Add("q", $"%{trimmedQuery}%");
                }
            }
            else
            {
                where.Add("(title LIKE @q OR releases.title_clean LIKE @q)");
                args.Add("q", $"%{trimmedQuery}%");
            }
        }

        if (categoryId is not null)
        {
            where.Add("category_id = @cat");
            args.Add("cat", categoryId.Value);
        }

        if (minSeeders is not null)
        {
            where.Add("(seeders IS NOT NULL AND seeders >= @ms)");
            args.Add("ms", minSeeders.Value);
        }

        if (seen is not null)
        {
            where.Add("seen = @seen");
            args.Add("seen", seen.Value == 1 ? 1 : 0);
        }

        var sql = hasCategories ? $"""
        SELECT
          releases.id as id,
          releases.source_id as sourceId,
          title,
          releases.title_clean as titleClean,
          releases.year,
          season,
          episode,
          resolution,
          source,
          codec,
          release_group as releaseGroup,
          media_type as mediaType,

          seeders,
          leechers,
          grabs,
          size_bytes as sizeBytes,
          published_at_ts as publishedAt,
          releases.category_id as categoryId,
          sc.name as categoryName,
          std_category_id as stdCategoryId,
          spec_category_id as specCategoryId,
          category_ids as categoryIds,
          releases.unified_category as unifiedCategory,
          sc.unified_key as unifiedCategoryKey,
          sc.unified_label as unifiedCategoryLabel,
          seen,

          ('/api/releases/' || releases.id || '/download') as downloadPath,
          releases.entity_id as entityId,
          CASE
              WHEN COALESCE(releases.poster_file, me.poster_file) IS NOT NULL
                   AND COALESCE(releases.poster_file, me.poster_file) <> ''
              THEN ('/api/posters/release/' || releases.id || '?v=' || COALESCE(releases.poster_updated_at_ts, me.poster_updated_at_ts, 0))
                        ELSE NULL
          END as posterUrl
          ,COALESCE(releases.poster_updated_at_ts, me.poster_updated_at_ts, 0) as posterUpdatedAtTs
          ,poster_provider as posterProvider
          ,poster_lang as posterLang
          ,poster_size as posterSize
          ,poster_last_error as posterLastError
          ,poster_last_attempt_ts as posterLastAttemptTs
          ,COALESCE(me.ext_overview, releases.ext_overview) as overview
          ,COALESCE(me.ext_tagline, releases.ext_tagline) as tagline
          ,COALESCE(me.ext_genres, releases.ext_genres) as genres
          ,COALESCE(me.ext_release_date, releases.ext_release_date) as releaseDate
          ,CAST(COALESCE(me.ext_runtime_minutes, releases.ext_runtime_minutes) AS INTEGER) as runtimeMinutes
          ,COALESCE(me.ext_rating, releases.ext_rating) as rating
          ,CAST(COALESCE(me.ext_votes, releases.ext_votes) AS INTEGER) as ratingVotes
          ,COALESCE(me.ext_provider, releases.ext_provider) as detailsProvider
          ,COALESCE(me.ext_provider_id, releases.ext_provider_id) as detailsProviderId
          ,COALESCE(me.ext_updated_at_ts, releases.ext_updated_at_ts) as detailsUpdatedAtTs
          ,COALESCE(me.ext_directors, releases.ext_directors) as directors
          ,COALESCE(me.ext_writers, releases.ext_writers) as writers
          ,COALESCE(me.ext_cast, releases.ext_cast) as cast
          ,CAST(COALESCE(me.tmdb_id, releases.tmdb_id) AS INTEGER) as tmdbId
          ,CAST(COALESCE(me.tvdb_id, releases.tvdb_id) AS INTEGER) as tvdbId
          ,ras.in_sonarr as isInSonarr
          ,ras.in_radarr as isInRadarr
          ,ras.sonarr_url as sonarrUrl
          ,ras.radarr_url as radarrUrl
          ,COALESCE(ras.sonarr_url, ras.radarr_url) as openUrl
          ,ras.checked_at_ts as arrCheckedAtTs
        FROM releases
        LEFT JOIN media_entities me
          ON me.id = releases.entity_id
        INNER JOIN source_categories sc
          ON sc.source_id = releases.source_id AND sc.cat_id = releases.category_id
        LEFT JOIN release_arr_status ras
          ON ras.release_id = releases.id
        WHERE {string.Join(" AND ", where)}
        ORDER BY published_at_ts DESC
        LIMIT @lim;
        """
        : $"""
        SELECT
          releases.id as id,
          releases.source_id as sourceId,
          title,
          releases.title_clean as titleClean,
          releases.year,
          season,
          episode,
          resolution,
          source,
          codec,
          release_group as releaseGroup,
          media_type as mediaType,

          seeders,
          leechers,
          grabs,
          size_bytes as sizeBytes,
          published_at_ts as publishedAt,
          releases.category_id as categoryId,
          std_category_id as stdCategoryId,
          spec_category_id as specCategoryId,
          category_ids as categoryIds,
          releases.unified_category as unifiedCategory,
          NULL as categoryName,
          seen,

          ('/api/releases/' || releases.id || '/download') as downloadPath,
          releases.entity_id as entityId,
          CASE
              WHEN COALESCE(releases.poster_file, me.poster_file) IS NOT NULL
                   AND COALESCE(releases.poster_file, me.poster_file) <> ''
              THEN ('/api/posters/release/' || releases.id || '?v=' || COALESCE(releases.poster_updated_at_ts, me.poster_updated_at_ts, 0))
                        ELSE NULL
          END as posterUrl
          ,COALESCE(releases.poster_updated_at_ts, me.poster_updated_at_ts, 0) as posterUpdatedAtTs
          ,poster_provider as posterProvider
          ,poster_lang as posterLang
          ,poster_size as posterSize
          ,poster_last_error as posterLastError
          ,poster_last_attempt_ts as posterLastAttemptTs
          ,COALESCE(me.ext_overview, releases.ext_overview) as overview
          ,COALESCE(me.ext_tagline, releases.ext_tagline) as tagline
          ,COALESCE(me.ext_genres, releases.ext_genres) as genres
          ,COALESCE(me.ext_release_date, releases.ext_release_date) as releaseDate
          ,CAST(COALESCE(me.ext_runtime_minutes, releases.ext_runtime_minutes) AS INTEGER) as runtimeMinutes
          ,COALESCE(me.ext_rating, releases.ext_rating) as rating
          ,CAST(COALESCE(me.ext_votes, releases.ext_votes) AS INTEGER) as ratingVotes
          ,COALESCE(me.ext_provider, releases.ext_provider) as detailsProvider
          ,COALESCE(me.ext_provider_id, releases.ext_provider_id) as detailsProviderId
          ,COALESCE(me.ext_updated_at_ts, releases.ext_updated_at_ts) as detailsUpdatedAtTs
          ,COALESCE(me.ext_directors, releases.ext_directors) as directors
          ,COALESCE(me.ext_writers, releases.ext_writers) as writers
          ,COALESCE(me.ext_cast, releases.ext_cast) as cast
          ,CAST(COALESCE(me.tmdb_id, releases.tmdb_id) AS INTEGER) as tmdbId
          ,CAST(COALESCE(me.tvdb_id, releases.tvdb_id) AS INTEGER) as tvdbId
          ,ras.in_sonarr as isInSonarr
          ,ras.in_radarr as isInRadarr
          ,ras.sonarr_url as sonarrUrl
          ,ras.radarr_url as radarrUrl
          ,COALESCE(ras.sonarr_url, ras.radarr_url) as openUrl
          ,ras.checked_at_ts as arrCheckedAtTs
        FROM releases
        LEFT JOIN media_entities me
          ON me.id = releases.entity_id
        LEFT JOIN release_arr_status ras
          ON ras.release_id = releases.id
        WHERE {string.Join(" AND ", where)}
        ORDER BY published_at_ts DESC
        LIMIT @lim;
        """;

        var rows = conn.Query<FeedRow>(sql, args).ToList();
        foreach (var row in rows)
        {
            var hasMappedKey = !string.IsNullOrWhiteSpace(row.UnifiedCategoryKey);
            if (hasMappedKey && string.IsNullOrWhiteSpace(row.UnifiedCategoryLabel) &&
                UnifiedCategoryMappings.TryParseKey(row.UnifiedCategoryKey, out var mappedFromKey))
            {
                row.UnifiedCategoryLabel = UnifiedCategoryMappings.ToLabel(mappedFromKey);
            }
            if (!hasMappedKey &&
                UnifiedCategoryMappings.TryParse(row.UnifiedCategory, out var unifiedCategory) &&
                unifiedCategory != UnifiedCategory.Autre)
            {
                row.UnifiedCategoryKey = UnifiedCategoryMappings.ToKey(unifiedCategory);
                row.UnifiedCategoryLabel = UnifiedCategoryMappings.ToLabel(unifiedCategory);
            }
            else
            {
                var unified = _unified.Get(row.CategoryName, row.TitleClean ?? row.Title);
                var overrideKey = unified?.Key is "shows" or "spectacle";

                if (!hasMappedKey && (overrideKey || string.IsNullOrWhiteSpace(row.UnifiedCategoryKey)))
                {
                    row.UnifiedCategoryKey = unified?.Key;
                    row.UnifiedCategoryLabel = unified?.Label;
                }
            }
        }

        return Ok(rows);
    }

    // GET /api/feed/top?sourceId=1&limit=5&sortBy=seeders|rating|downloads|recent
    [EnableRateLimiting("stats-heavy")]
    [HttpGet("top")]
    public IActionResult Top([FromQuery] long? sourceId, [FromQuery] int? limit, [FromQuery] string? sortBy, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var lim = Math.Clamp(limit ?? 5, 1, 20);
        var topSort = NormalizeTopSort(sortBy);
        var cacheKey = $"feed:top:v2:{sourceId?.ToString() ?? "all"}:{lim}:{topSort}";
        if (_cache.TryGetValue<object>(cacheKey, out var cached) && cached is not null)
            return Ok(cached);

        var topWhere = GetTopSortWhereClause(topSort);
        var topOrder = GetTopSortOrderClause(topSort);

        using var conn = _db.Open();
        var hasCategories = conn.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='source_categories';"
        ) > 0;

        if (!hasCategories)
        {
            return Ok(new
            {
                global = Array.Empty<object>(),
                byCategory = new Dictionary<string, object[]>()
            });
        }

        var categories = new[] { "films", "series", "anime", "games", "shows", "spectacle" };
        var result = new Dictionary<string, object>
        {
            ["sortBy"] = topSort
        };

        // Top global (toutes catégories, TOUJOURS tous les indexeurs, dernières 24h)
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var twentyFourHoursAgo = now - (24 * 60 * 60);

        var globalSql = $"""
        SELECT
          releases.id as id,
          releases.source_id as sourceId,
          title,
          releases.title_clean as titleClean,
          releases.year,
          season,
          episode,
          resolution,
          source,
          codec,
          release_group as releaseGroup,
          media_type as mediaType,
          seeders,
          leechers,
          grabs,
          size_bytes as sizeBytes,
          published_at_ts as publishedAt,
          releases.category_id as categoryId,
          sc.name as categoryName,
          std_category_id as stdCategoryId,
          spec_category_id as specCategoryId,
          category_ids as categoryIds,
          releases.unified_category as unifiedCategory,
          sc.unified_key as unifiedCategoryKey,
          sc.unified_label as unifiedCategoryLabel,
          seen,
          ('/api/releases/' || releases.id || '/download') as downloadPath,
          releases.entity_id as entityId,
          CASE
              WHEN COALESCE(releases.poster_file, me.poster_file) IS NOT NULL
                   AND COALESCE(releases.poster_file, me.poster_file) <> ''
              THEN ('/api/posters/release/' || releases.id || '?v=' || COALESCE(releases.poster_updated_at_ts, me.poster_updated_at_ts, 0))
              ELSE NULL
          END as posterUrl
          ,COALESCE(releases.poster_updated_at_ts, me.poster_updated_at_ts, 0) as posterUpdatedAtTs
          ,poster_last_error as posterLastError
          ,poster_last_attempt_ts as posterLastAttemptTs
          ,COALESCE(me.ext_overview, releases.ext_overview) as overview
          ,COALESCE(me.ext_tagline, releases.ext_tagline) as tagline
          ,COALESCE(me.ext_genres, releases.ext_genres) as genres
          ,COALESCE(me.ext_release_date, releases.ext_release_date) as releaseDate
          ,CAST(COALESCE(me.ext_runtime_minutes, releases.ext_runtime_minutes) AS INTEGER) as runtimeMinutes
          ,COALESCE(me.ext_rating, releases.ext_rating) as rating
          ,CAST(COALESCE(me.ext_votes, releases.ext_votes) AS INTEGER) as ratingVotes
          ,COALESCE(me.ext_provider, releases.ext_provider) as detailsProvider
          ,COALESCE(me.ext_provider_id, releases.ext_provider_id) as detailsProviderId
          ,COALESCE(me.ext_updated_at_ts, releases.ext_updated_at_ts) as detailsUpdatedAtTs
          ,COALESCE(me.ext_directors, releases.ext_directors) as directors
          ,COALESCE(me.ext_writers, releases.ext_writers) as writers
          ,COALESCE(me.ext_cast, releases.ext_cast) as cast
          ,CAST(COALESCE(me.tmdb_id, releases.tmdb_id) AS INTEGER) as tmdbId
          ,CAST(COALESCE(me.tvdb_id, releases.tvdb_id) AS INTEGER) as tvdbId
          ,ras.in_sonarr as isInSonarr
          ,ras.in_radarr as isInRadarr
          ,ras.sonarr_url as sonarrUrl
          ,ras.radarr_url as radarrUrl
          ,COALESCE(ras.sonarr_url, ras.radarr_url) as openUrl
          ,ras.checked_at_ts as arrCheckedAtTs
        FROM releases
        LEFT JOIN media_entities me
          ON me.id = releases.entity_id
        INNER JOIN source_categories sc
          ON sc.source_id = releases.source_id AND sc.cat_id = releases.category_id
        LEFT JOIN release_arr_status ras
          ON ras.release_id = releases.id
        WHERE {topWhere}
          AND published_at_ts >= @minTs
        ORDER BY {topOrder}
        LIMIT @lim;
        """;

        var globalArgs = new DynamicParameters();
        globalArgs.Add("lim", lim);
        globalArgs.Add("minTs", twentyFourHoursAgo);

        var globalRows = conn.Query<FeedRow>(globalSql, globalArgs).ToList();
        foreach (var row in globalRows)
        {
            var hasMappedKey = !string.IsNullOrWhiteSpace(row.UnifiedCategoryKey);
            if (!hasMappedKey &&
                UnifiedCategoryMappings.TryParse(row.UnifiedCategory, out var unifiedCategory) &&
                unifiedCategory != UnifiedCategory.Autre)
            {
                row.UnifiedCategoryKey = UnifiedCategoryMappings.ToKey(unifiedCategory);
                row.UnifiedCategoryLabel = UnifiedCategoryMappings.ToLabel(unifiedCategory);
            }
            else
            {
                var unified = _unified.Get(row.CategoryName, row.TitleClean ?? row.Title);
                var overrideKey = unified?.Key is "shows" or "spectacle";
                if (!hasMappedKey && (overrideKey || string.IsNullOrWhiteSpace(row.UnifiedCategoryKey)))
                {
                    row.UnifiedCategoryKey = unified?.Key;
                    row.UnifiedCategoryLabel = unified?.Label;
                }
            }
        }
        result["global"] = globalRows;

        // Top par catégorie
        var byCategory = new Dictionary<string, object>();
        foreach (var cat in categories)
        {
            var catWhere = sourceId.HasValue
                ? "sc.source_id = @sid AND sc.unified_key = @cat"
                : "sc.unified_key = @cat";

            var catSql = $"""
            SELECT
              releases.id as id,
              releases.source_id as sourceId,
              title,
              releases.title_clean as titleClean,
              releases.year,
              season,
              episode,
              resolution,
              source,
              codec,
              release_group as releaseGroup,
              media_type as mediaType,
              seeders,
              leechers,
              grabs,
              size_bytes as sizeBytes,
              published_at_ts as publishedAt,
              releases.category_id as categoryId,
              sc.name as categoryName,
              std_category_id as stdCategoryId,
              spec_category_id as specCategoryId,
              category_ids as categoryIds,
              releases.unified_category as unifiedCategory,
              sc.unified_key as unifiedCategoryKey,
              sc.unified_label as unifiedCategoryLabel,
              seen,
              ('/api/releases/' || releases.id || '/download') as downloadPath,
              releases.entity_id as entityId,
              CASE
                  WHEN COALESCE(releases.poster_file, me.poster_file) IS NOT NULL
                       AND COALESCE(releases.poster_file, me.poster_file) <> ''
                  THEN ('/api/posters/release/' || releases.id || '?v=' || COALESCE(releases.poster_updated_at_ts, me.poster_updated_at_ts, 0))
                  ELSE NULL
              END as posterUrl
              ,COALESCE(releases.poster_updated_at_ts, me.poster_updated_at_ts, 0) as posterUpdatedAtTs
              ,poster_last_error as posterLastError
              ,poster_last_attempt_ts as posterLastAttemptTs
              ,COALESCE(me.ext_overview, releases.ext_overview) as overview
              ,COALESCE(me.ext_tagline, releases.ext_tagline) as tagline
              ,COALESCE(me.ext_genres, releases.ext_genres) as genres
              ,COALESCE(me.ext_release_date, releases.ext_release_date) as releaseDate
              ,CAST(COALESCE(me.ext_runtime_minutes, releases.ext_runtime_minutes) AS INTEGER) as runtimeMinutes
              ,COALESCE(me.ext_rating, releases.ext_rating) as rating
              ,CAST(COALESCE(me.ext_votes, releases.ext_votes) AS INTEGER) as ratingVotes
              ,COALESCE(me.ext_provider, releases.ext_provider) as detailsProvider
              ,COALESCE(me.ext_provider_id, releases.ext_provider_id) as detailsProviderId
              ,COALESCE(me.ext_updated_at_ts, releases.ext_updated_at_ts) as detailsUpdatedAtTs
              ,COALESCE(me.ext_directors, releases.ext_directors) as directors
              ,COALESCE(me.ext_writers, releases.ext_writers) as writers
              ,COALESCE(me.ext_cast, releases.ext_cast) as cast
              ,CAST(COALESCE(me.tmdb_id, releases.tmdb_id) AS INTEGER) as tmdbId
              ,CAST(COALESCE(me.tvdb_id, releases.tvdb_id) AS INTEGER) as tvdbId
              ,ras.in_sonarr as isInSonarr
              ,ras.in_radarr as isInRadarr
              ,ras.sonarr_url as sonarrUrl
              ,ras.radarr_url as radarrUrl
              ,COALESCE(ras.sonarr_url, ras.radarr_url) as openUrl
              ,ras.checked_at_ts as arrCheckedAtTs
            FROM releases
            LEFT JOIN media_entities me
              ON me.id = releases.entity_id
            INNER JOIN source_categories sc
              ON sc.source_id = releases.source_id AND sc.cat_id = releases.category_id
            LEFT JOIN release_arr_status ras
              ON ras.release_id = releases.id
            WHERE {catWhere}
              AND {topWhere}
              AND published_at_ts >= @minTs
            ORDER BY {topOrder}
            LIMIT @lim;
            """;

            var catArgs = new DynamicParameters();
            catArgs.Add("cat", cat);
            catArgs.Add("lim", lim);
            catArgs.Add("minTs", twentyFourHoursAgo);
            if (sourceId.HasValue) catArgs.Add("sid", sourceId.Value);

            var catRows = conn.Query<FeedRow>(catSql, catArgs).ToList();
            foreach (var row in catRows)
            {
                var hasMappedKey = !string.IsNullOrWhiteSpace(row.UnifiedCategoryKey);
                if (hasMappedKey && string.IsNullOrWhiteSpace(row.UnifiedCategoryLabel) &&
                    UnifiedCategoryMappings.TryParseKey(row.UnifiedCategoryKey, out var mappedFromKey))
                {
                    row.UnifiedCategoryLabel = UnifiedCategoryMappings.ToLabel(mappedFromKey);
                }
                if (!hasMappedKey &&
                    UnifiedCategoryMappings.TryParse(row.UnifiedCategory, out var unifiedCategory) &&
                    unifiedCategory != UnifiedCategory.Autre)
                {
                    row.UnifiedCategoryKey = UnifiedCategoryMappings.ToKey(unifiedCategory);
                    row.UnifiedCategoryLabel = UnifiedCategoryMappings.ToLabel(unifiedCategory);
                }
                else
                {
                    var unified = _unified.Get(row.CategoryName, row.TitleClean ?? row.Title);
                    var overrideKey = unified?.Key is "shows" or "spectacle";
                    if (!hasMappedKey && (overrideKey || string.IsNullOrWhiteSpace(row.UnifiedCategoryKey)))
                    {
                        row.UnifiedCategoryKey = unified?.Key;
                        row.UnifiedCategoryLabel = unified?.Label;
                    }
                }
            }
            byCategory[cat] = catRows;
        }
        result["byCategory"] = byCategory;

        _cache.Set(cacheKey, result, TopCacheDuration);
        return Ok(result);
    }
}
