using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Matching;
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
    private static readonly TimeSpan TopCacheDuration = TimeSpan.FromSeconds(20);
    private static readonly Regex SearchTokenRegex = new(@"[a-zA-Z0-9_-]+", RegexOptions.Compiled);
    private const int MaxFtsTokenLength = 64;
    private const int TopMaxHours = 24 * 7;
    private const int TopMaxTake = 50;
    private const string TopWindowField = "published_at_ts";
    private const string TopOrderSql = "releases.published_at_ts DESC, releases.id DESC";
    private const string UnifiedCategoryToKeySql =
        "CASE releases.unified_category " +
        "WHEN 'Film' THEN 'films' " +
        "WHEN 'Serie' THEN 'series' " +
        "WHEN 'Emission' THEN 'emissions' " +
        "WHEN 'Spectacle' THEN 'spectacle' " +
        "WHEN 'JeuWindows' THEN 'games' " +
        "WHEN 'Animation' THEN 'animation' " +
        "WHEN 'Anime' THEN 'anime' " +
        "WHEN 'Audio' THEN 'audio' " +
        "WHEN 'Book' THEN 'books' " +
        "WHEN 'Comic' THEN 'comics' " +
        "ELSE NULL END";
    private const string MappingKeyToCanonicalSql =
        "CASE lower(trim(COALESCE(scm.group_key, ''))) " +
        "WHEN '' THEN NULL " +
        "WHEN 'movie' THEN 'films' " +
        "WHEN 'movies' THEN 'films' " +
        "WHEN 'film' THEN 'films' " +
        "WHEN 'films' THEN 'films' " +
        "WHEN 'tv' THEN 'series' " +
        "WHEN 'serie' THEN 'series' " +
        "WHEN 'series' THEN 'series' " +
        "WHEN 'show' THEN 'emissions' " +
        "WHEN 'shows' THEN 'emissions' " +
        "WHEN 'emission' THEN 'emissions' " +
        "WHEN 'emissions' THEN 'emissions' " +
        "WHEN 'game' THEN 'games' " +
        "WHEN 'games' THEN 'games' " +
        "WHEN 'book' THEN 'books' " +
        "WHEN 'books' THEN 'books' " +
        "WHEN 'comic' THEN 'comics' " +
        "WHEN 'comics' THEN 'comics' " +
        "WHEN 'animation' THEN 'animation' " +
        "WHEN 'anime' THEN 'anime' " +
        "WHEN 'audio' THEN 'audio' " +
        "WHEN 'spectacle' THEN 'spectacle' " +
        "ELSE lower(trim(scm.group_key)) END";
    private const string LegacySourceCategoryKeyToCanonicalSql =
        "CASE lower(trim(COALESCE(sc.unified_key, ''))) " +
        "WHEN '' THEN NULL " +
        "WHEN 'movie' THEN 'films' " +
        "WHEN 'movies' THEN 'films' " +
        "WHEN 'film' THEN 'films' " +
        "WHEN 'films' THEN 'films' " +
        "WHEN 'tv' THEN 'series' " +
        "WHEN 'serie' THEN 'series' " +
        "WHEN 'series' THEN 'series' " +
        "WHEN 'show' THEN 'emissions' " +
        "WHEN 'shows' THEN 'emissions' " +
        "WHEN 'emission' THEN 'emissions' " +
        "WHEN 'emissions' THEN 'emissions' " +
        "WHEN 'game' THEN 'games' " +
        "WHEN 'games' THEN 'games' " +
        "WHEN 'book' THEN 'books' " +
        "WHEN 'books' THEN 'books' " +
        "WHEN 'comic' THEN 'comics' " +
        "WHEN 'comics' THEN 'comics' " +
        "WHEN 'animation' THEN 'animation' " +
        "WHEN 'anime' THEN 'anime' " +
        "WHEN 'audio' THEN 'audio' " +
        "WHEN 'spectacle' THEN 'spectacle' " +
        "ELSE lower(trim(sc.unified_key)) END";
    private const string EffectiveTopCategoryKeySql =
        "COALESCE(" + UnifiedCategoryToKeySql + ", " + MappingKeyToCanonicalSql + ", " + LegacySourceCategoryKeyToCanonicalSql + ")";
    private const string TopFallbackCategoryKeySql =
        "CASE " +
        "WHEN lower(COALESCE(releases.title_clean, releases.title, '')) LIKE '%spectacle%' " +
        "  OR lower(COALESCE(releases.title_clean, releases.title, '')) LIKE '%concert%' " +
        "  OR lower(COALESCE(releases.title_clean, releases.title, '')) LIKE '%opera%' " +
        "  OR lower(COALESCE(releases.title_clean, releases.title, '')) LIKE '%theatre%' " +
        "  OR lower(COALESCE(releases.title_clean, releases.title, '')) LIKE '%ballet%' " +
        "THEN 'spectacle' " +
        "WHEN lower(COALESCE(releases.title_clean, releases.title, '')) LIKE '%emission%' " +
        "  OR lower(COALESCE(releases.title_clean, releases.title, '')) LIKE '%enquete%' " +
        "  OR lower(COALESCE(releases.title_clean, releases.title, '')) LIKE '%magazine%' " +
        "  OR lower(COALESCE(releases.title_clean, releases.title, '')) LIKE '%talk%' " +
        "  OR lower(COALESCE(releases.title_clean, releases.title, '')) LIKE '%show%' " +
        "  OR lower(COALESCE(releases.title_clean, releases.title, '')) LIKE '%reportage%' " +
        "  OR lower(COALESCE(releases.title_clean, releases.title, '')) LIKE '%documentaire%' " +
        "  OR lower(COALESCE(releases.title_clean, releases.title, '')) LIKE '%docu%' " +
        "  OR lower(COALESCE(releases.title_clean, releases.title, '')) LIKE '%quotidien%' " +
        "THEN 'emissions' " +
        "WHEN lower(COALESCE(sc.name, '')) LIKE '%pc/games%' " +
        "  OR lower(COALESCE(sc.name, '')) LIKE '%pc games%' " +
        "  OR lower(COALESCE(sc.name, '')) LIKE '%game%' " +
        "  OR lower(COALESCE(sc.name, '')) LIKE '%jeu%' " +
        "THEN 'games' " +
        "WHEN lower(COALESCE(sc.name, '')) LIKE '%anime%' THEN 'anime' " +
        "WHEN lower(COALESCE(sc.name, '')) LIKE '%audio%' " +
        "  OR lower(COALESCE(sc.name, '')) LIKE '%music%' " +
        "  OR lower(COALESCE(sc.name, '')) LIKE '%musique%' " +
        "  OR lower(COALESCE(sc.name, '')) LIKE '%podcast%' " +
        "THEN 'audio' " +
        "WHEN lower(COALESCE(sc.name, '')) LIKE '%book%' " +
        "  OR lower(COALESCE(sc.name, '')) LIKE '%livre%' " +
        "  OR lower(COALESCE(sc.name, '')) LIKE '%ebook%' " +
        "THEN 'books' " +
        "WHEN lower(COALESCE(sc.name, '')) LIKE '%comic%' " +
        "  OR lower(COALESCE(sc.name, '')) LIKE '%manga%' " +
        "  OR lower(COALESCE(sc.name, '')) LIKE '%scan%' " +
        "THEN 'comics' " +
        "WHEN lower(COALESCE(sc.name, '')) LIKE '%spectacle%' " +
        "  OR lower(COALESCE(sc.name, '')) LIKE '%concert%' " +
        "  OR lower(COALESCE(sc.name, '')) LIKE '%opera%' " +
        "  OR lower(COALESCE(sc.name, '')) LIKE '%theatre%' " +
        "  OR lower(COALESCE(sc.name, '')) LIKE '%ballet%' " +
        "THEN 'spectacle' " +
        "WHEN lower(COALESCE(sc.name, '')) LIKE '%documentary%' " +
        "  OR lower(COALESCE(sc.name, '')) LIKE '%documentaire%' " +
        "  OR lower(COALESCE(sc.name, '')) LIKE '%doc%' " +
        "  OR lower(COALESCE(sc.name, '')) LIKE '%emission%' " +
        "  OR lower(COALESCE(sc.name, '')) LIKE '%show%' " +
        "  OR lower(COALESCE(sc.name, '')) LIKE '%magazine%' " +
        "THEN 'emissions' " +
        "WHEN lower(COALESCE(sc.name, '')) = 'movies' " +
        "  OR lower(COALESCE(sc.name, '')) LIKE 'movie%' " +
        "THEN 'films' " +
        "WHEN lower(COALESCE(sc.name, '')) = 'tv' " +
        "  OR lower(COALESCE(sc.name, '')) LIKE 'tv/%' " +
        "THEN 'series' " +
        "ELSE NULL END";
    private const string ResolvedTopCategoryKeySql =
        "COALESCE(" + EffectiveTopCategoryKeySql + ", " + TopFallbackCategoryKeySql + ")";
    private const string ResolvedTopCategoryLabelSql =
        "CASE " + ResolvedTopCategoryKeySql + " " +
        "WHEN 'films' THEN 'Films' " +
        "WHEN 'series' THEN 'Série TV' " +
        "WHEN 'emissions' THEN 'Emissions' " +
        "WHEN 'spectacle' THEN 'Spectacle' " +
        "WHEN 'games' THEN 'Jeux PC' " +
        "WHEN 'animation' THEN 'Animation' " +
        "WHEN 'anime' THEN 'Anime' " +
        "WHEN 'audio' THEN 'Audio' " +
        "WHEN 'books' THEN 'Livres' " +
        "WHEN 'comics' THEN 'Comics' " +
        "ELSE COALESCE(scm.group_label, sc.unified_label, " + ResolvedTopCategoryKeySql + ") END";

    public FeedController(Db db, UnifiedCategoryService unified, IMemoryCache cache)
    {
        _db = db;
        _unified = unified;
        _cache = cache;
    }

    private class FeedRow
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

    private sealed class TopCategoryRow : FeedRow
    {
        public int CatCount { get; set; }
    }

    private static string? CanonicalizeUnifiedCategoryKey(string? key)
    {
        return CategoryGroupCatalog.TryNormalizeKey(key, out var canonicalKey)
            ? canonicalKey
            : null;
    }

    private void PopulateUnifiedCategoryMetadata(FeedRow row)
    {
        var hasMappedKey = !string.IsNullOrWhiteSpace(row.UnifiedCategoryKey);
        if (hasMappedKey &&
            UnifiedCategoryMappings.TryParseKey(row.UnifiedCategoryKey, out var mappedFromKey))
        {
            row.UnifiedCategoryKey = UnifiedCategoryMappings.ToKey(mappedFromKey);
            row.UnifiedCategoryLabel = UnifiedCategoryMappings.ToLabel(mappedFromKey);
        }
        else if (!hasMappedKey &&
                 UnifiedCategoryMappings.TryParse(row.UnifiedCategory, out var unifiedCategory) &&
                 unifiedCategory != UnifiedCategory.Autre)
        {
            row.UnifiedCategoryKey = UnifiedCategoryMappings.ToKey(unifiedCategory);
            row.UnifiedCategoryLabel = UnifiedCategoryMappings.ToLabel(unifiedCategory);
        }
        else
        {
            var unified = _unified.Get(row.CategoryName, row.TitleClean ?? row.Title);
            var overrideKey = unified?.Key is "shows" or "spectacle" or "audio" or "books" or "comics";

            if (!hasMappedKey && (overrideKey || string.IsNullOrWhiteSpace(row.UnifiedCategoryKey)))
            {
                row.UnifiedCategoryKey = unified?.Key;
                row.UnifiedCategoryLabel = unified?.Label;
            }
        }

        var canonicalKey = CanonicalizeUnifiedCategoryKey(row.UnifiedCategoryKey);
        if (!string.IsNullOrWhiteSpace(canonicalKey))
        {
            row.UnifiedCategoryKey = canonicalKey;
            row.UnifiedCategoryLabel = CategoryGroupCatalog.LabelForKey(canonicalKey);
        }
        else if (!string.IsNullOrWhiteSpace(row.UnifiedCategoryKey))
        {
            row.UnifiedCategoryKey = null;
        }
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

    private static string NormalizeTopSort(string? sort)
    {
        return (sort ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "grabs" => "grabs",
            "seeders" => "seeders",
            "score" => "score",
            _ => "recent"
        };
    }

    private static string NormalizeTopDedupe(string? dedupe, bool supportsEntityDedupe)
    {
        if (string.IsNullOrWhiteSpace(dedupe))
            return "none";

        return dedupe.Trim().ToLowerInvariant() switch
        {
            "none" => "none",
            "entity" => supportsEntityDedupe ? "entity" : "title_year",
            "title" => "title",
            "title_year" => "title_year",
            _ => supportsEntityDedupe ? "entity" : "title_year"
        };
    }

    private static string BuildTopSortOrderSql(string alias, string sort)
    {
        return sort switch
        {
            "grabs" => $"COALESCE({alias}.grabs, 0) DESC, COALESCE({alias}.seeders, 0) DESC, COALESCE({alias}.publishedAt, 0) DESC, {alias}.id DESC",
            "seeders" => $"COALESCE({alias}.seeders, 0) DESC, COALESCE({alias}.grabs, 0) DESC, COALESCE({alias}.publishedAt, 0) DESC, {alias}.id DESC",
            "score" => $"COALESCE({alias}.topScore, 0) DESC, COALESCE({alias}.publishedAt, 0) DESC, {alias}.id DESC",
            _ => $"COALESCE({alias}.publishedAt, 0) DESC, {alias}.id DESC"
        };
    }

    private static string BuildTopDedupeKeySql(string alias, string dedupe)
    {
        return dedupe switch
        {
            "entity" =>
                $"CASE WHEN {alias}.entityId IS NOT NULL AND {alias}.entityId > 0 THEN ('entity:' || CAST({alias}.entityId AS TEXT)) ELSE ('release:' || CAST({alias}.id AS TEXT)) END",
            "title_year" =>
                $"CASE WHEN COALESCE({alias}.dedupeTitle, '') <> '' THEN ('title_year:' || {alias}.dedupeTitle || '|' || COALESCE(CAST({alias}.year AS TEXT), '-') || '|' || lower(trim(COALESCE({alias}.mediaType, '')))) ELSE ('release:' || CAST({alias}.id AS TEXT)) END",
            "title" =>
                $"CASE WHEN COALESCE({alias}.dedupeTitle, '') <> '' THEN ('title:' || {alias}.dedupeTitle) ELSE ('release:' || CAST({alias}.id AS TEXT)) END",
            _ => $"'release:' || CAST({alias}.id AS TEXT)"
        };
    }

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
        LEFT JOIN source_categories sc
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
            PopulateUnifiedCategoryMetadata(row);
        }

        return Ok(rows);
    }

    // GET /api/feed/top?hours=24&take=5&sourceId=1
    [EnableRateLimiting("stats-heavy")]
    [HttpGet("top")]
    public IActionResult Top(
        [FromQuery] int? hours,
        [FromQuery] int? take,
        [FromQuery] int? perCategoryTake,
        [FromQuery] string? sort,
        [FromQuery] string? dedupe,
        [FromQuery] int? limit,
        [FromQuery] long? sourceId,
        [FromQuery] long? indexerId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        const bool supportsEntityDedupe = true;
        var effectiveSourceId = sourceId ?? indexerId;
        var effectiveHours = Math.Clamp(hours ?? 24, 1, TopMaxHours);
        var effectiveTake = Math.Clamp(take ?? limit ?? 5, 1, TopMaxTake);
        var effectivePerCategoryTake = Math.Clamp(perCategoryTake ?? effectiveTake, 1, TopMaxTake);
        var effectiveSort = NormalizeTopSort(sort);
        // Keep omitted dedupe fully backward-compatible: no dedupe unless explicitly requested.
        var effectiveDedupe = NormalizeTopDedupe(dedupe, supportsEntityDedupe);
        var sinceTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (effectiveHours * 60L * 60L);
        var cacheKey = $"feed:top:v4:{effectiveSourceId?.ToString() ?? "all"}:{effectiveHours}:{effectiveTake}:{effectivePerCategoryTake}:{effectiveSort}:{effectiveDedupe}";
        if (_cache.TryGetValue<object>(cacheKey, out var cached) && cached is not null)
            return Ok(cached);

        using var conn = _db.Open();
        conn.CreateFunction<string, string>(
            "top_normalize_title",
            value => TitleNormalizer.NormalizeTitleStrict(value),
            isDeterministic: true);

        var sourceFilterSql = effectiveSourceId.HasValue
            ? "releases.source_id = @sid AND "
            : string.Empty;
        var topSortOrderSql = BuildTopSortOrderSql("deduped", effectiveSort);
        var topDedupeKeySql = BuildTopDedupeKeySql("base", effectiveDedupe);

        // Rank in SQL so the chosen business sort also drives dedupe selection and
        // the per-category top without materializing the whole window in memory.
        var baseCteSql = $"""
        WITH base AS (
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
            ({ResolvedTopCategoryKeySql}) as unifiedCategoryKey,
            ({ResolvedTopCategoryLabelSql}) as unifiedCategoryLabel,
            seen,
            ('/api/releases/' || releases.id || '/download') as downloadPath,
            releases.entity_id as entityId,
            CASE
                WHEN COALESCE(releases.poster_file, me.poster_file) IS NOT NULL
                     AND COALESCE(releases.poster_file, me.poster_file) <> ''
                THEN ('/api/posters/release/' || releases.id || '?v=' || COALESCE(releases.poster_updated_at_ts, me.poster_updated_at_ts, 0))
                ELSE NULL
            END as posterUrl,
            COALESCE(releases.poster_updated_at_ts, me.poster_updated_at_ts, 0) as posterUpdatedAtTs,
            poster_last_error as posterLastError,
            poster_last_attempt_ts as posterLastAttemptTs,
            COALESCE(me.ext_rating, releases.ext_rating) as rating,
            CAST(COALESCE(me.ext_votes, releases.ext_votes) AS INTEGER) as ratingVotes,
            COALESCE(me.ext_provider, releases.ext_provider) as detailsProvider,
            COALESCE(me.ext_provider_id, releases.ext_provider_id) as detailsProviderId,
            COALESCE(me.ext_updated_at_ts, releases.ext_updated_at_ts) as detailsUpdatedAtTs,
            CAST(COALESCE(me.tmdb_id, releases.tmdb_id) AS INTEGER) as tmdbId,
            CAST(COALESCE(me.tvdb_id, releases.tvdb_id) AS INTEGER) as tvdbId,
            ras.in_sonarr as isInSonarr,
            ras.in_radarr as isInRadarr,
            ras.sonarr_url as sonarrUrl,
            ras.radarr_url as radarrUrl,
            COALESCE(ras.sonarr_url, ras.radarr_url) as openUrl,
            ras.checked_at_ts as arrCheckedAtTs,
            top_normalize_title(COALESCE(releases.title_clean, releases.title, '')) as dedupeTitle,
            (COALESCE(releases.grabs, 0) * 5 + COALESCE(releases.seeders, 0)) as topScore
          FROM releases
          LEFT JOIN media_entities me
            ON me.id = releases.entity_id
          LEFT JOIN source_categories sc
            ON sc.source_id = releases.source_id AND sc.cat_id = releases.category_id
          LEFT JOIN source_category_mappings scm
            ON scm.source_id = releases.source_id AND scm.cat_id = releases.category_id
          LEFT JOIN release_arr_status ras
            ON ras.release_id = releases.id
          WHERE {sourceFilterSql}releases.{TopWindowField} >= @sinceTs
        ),
        deduped AS (
          SELECT *
          FROM (
            SELECT
              base.*,
              ROW_NUMBER() OVER (
                PARTITION BY {topDedupeKeySql}
                ORDER BY {BuildTopSortOrderSql("base", effectiveSort)}
              ) as dedupeRn
            FROM base
          ) ranked_dedupe
          WHERE ranked_dedupe.dedupeRn = 1
        )
        """;

        var globalArgs = new DynamicParameters();
        globalArgs.Add("sinceTs", sinceTs);
        globalArgs.Add("take", effectiveTake);
        if (effectiveSourceId.HasValue) globalArgs.Add("sid", effectiveSourceId.Value);

        var globalSql = baseCteSql + $"""
        SELECT *
        FROM (
          SELECT
            deduped.*,
            ROW_NUMBER() OVER (ORDER BY {topSortOrderSql}) as globalRn
          FROM deduped
        ) ranked_global
        WHERE ranked_global.globalRn <= @take
        ORDER BY ranked_global.globalRn ASC;
        """;

        var globalRows = conn.Query<FeedRow>(globalSql, globalArgs).ToList();
        foreach (var row in globalRows)
        {
            PopulateUnifiedCategoryMetadata(row);
        }

        var categoriesSql = baseCteSql + $"""
        SELECT *
        FROM (
          SELECT
            deduped.*,
            COUNT(1) OVER (
              PARTITION BY deduped.unifiedCategoryKey
            ) as catCount,
            ROW_NUMBER() OVER (
              PARTITION BY deduped.unifiedCategoryKey
              ORDER BY {topSortOrderSql}
            ) as catRn
          FROM deduped
          WHERE deduped.unifiedCategoryKey IS NOT NULL
        ) ranked_category
        WHERE ranked_category.catRn <= @perCategoryTake
        ORDER BY ranked_category.catCount DESC,
                 COALESCE(ranked_category.unifiedCategoryLabel, ranked_category.unifiedCategoryKey) ASC,
                 ranked_category.unifiedCategoryKey ASC,
                 ranked_category.catRn ASC;
        """;

        var categoryArgs = new DynamicParameters();
        categoryArgs.Add("sinceTs", sinceTs);
        categoryArgs.Add("perCategoryTake", effectivePerCategoryTake);
        if (effectiveSourceId.HasValue) categoryArgs.Add("sid", effectiveSourceId.Value);

        var categoryResults = new List<object>();
        string? currentKey = null;
        string? currentLabel = null;
        var currentCount = 0;
        List<FeedRow>? currentTop = null;

        foreach (var row in conn.Query<TopCategoryRow>(categoriesSql, categoryArgs))
        {
            PopulateUnifiedCategoryMetadata(row);
            if (string.IsNullOrWhiteSpace(row.UnifiedCategoryKey))
                continue;

            var key = row.UnifiedCategoryKey;
            var label = string.IsNullOrWhiteSpace(row.UnifiedCategoryLabel)
                ? key
                : row.UnifiedCategoryLabel;

            if (!string.Equals(currentKey, key, StringComparison.OrdinalIgnoreCase))
            {
                if (currentTop is not null && currentKey is not null && currentLabel is not null)
                {
                    categoryResults.Add(new
                    {
                        key = currentKey,
                        label = currentLabel,
                        count = currentCount,
                        top = currentTop
                    });
                }

                currentKey = key;
                currentLabel = label;
                currentCount = row.CatCount;
                currentTop = new List<FeedRow>(effectiveTake);
            }

            currentTop!.Add(row);
        }

        if (currentTop is not null && currentKey is not null && currentLabel is not null)
        {
            categoryResults.Add(new
            {
                key = currentKey,
                label = currentLabel,
                count = currentCount,
                top = currentTop
            });
        }

        var result = new
        {
            window = new
            {
                hours = effectiveHours,
                sinceUtc = DateTimeOffset.FromUnixTimeSeconds(sinceTs).UtcDateTime.ToString("O"),
                field = TopWindowField
            },
            takeUsed = effectiveTake,
            perCategoryTakeUsed = effectivePerCategoryTake,
            sortUsed = effectiveSort,
            dedupeUsed = effectiveDedupe,
            supportsEntityDedupe,
            globalTop = globalRows,
            categories = categoryResults
        };

        _cache.Set(cacheKey, result, TopCacheDuration);
        return Ok(result);
    }
}
