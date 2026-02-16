using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Torznab;
using Feedarr.Api.Services.Titles;
using System.Data;

namespace Feedarr.Api.Data.Repositories;

public sealed class ReleaseRepository
{
    private readonly Db _db;
    private readonly TitleParser _parser;
    private readonly UnifiedCategoryResolver _resolver;

    private static readonly string[] RetentionUnifiedCategories =
    {
        nameof(UnifiedCategory.Film),
        nameof(UnifiedCategory.Serie),
        nameof(UnifiedCategory.Emission),
        nameof(UnifiedCategory.Spectacle),
        nameof(UnifiedCategory.JeuWindows),
        nameof(UnifiedCategory.Animation)
    };

    public ReleaseRepository(Db db, TitleParser parser, UnifiedCategoryResolver resolver)
    {
        _db = db;
        _parser = parser;
        _resolver = resolver;
    }

    public int UpsertMany(
        long sourceId,
        string indexerKey,
        IEnumerable<TorznabItem> items,
        long? createdAtTs = null,
        int defaultSeen = 0,
        Dictionary<int, (string key, string label)>? categoryMap = null)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        var itemList = items as IList<TorznabItem> ?? items.ToList();
        var now = createdAtTs ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var sql = $"""
        INSERT INTO releases(
          source_id, guid, title, link, published_at_ts,
                    size_bytes, seeders, leechers, grabs, info_hash, download_url, category_id,
                    std_category_id, spec_category_id, unified_category, category_ids,
          raw_json, seen, created_at_ts,

          title_clean, year, season, episode, resolution, source, codec, release_group, media_type
        )
        VALUES(
          @sourceId, @guid, @title, @link, @pub,
                    @size, @seed, @leech, @grabs, @hash, @dl, @cat,
                    @stdCat, @specCat, @unifiedCat, @catIds,
          NULL, {defaultSeen}, @now,

          @titleClean, @year, @season, @episode, @resolution, @source, @codec, @group, @mediaType
        )
        ON CONFLICT(source_id, guid) DO UPDATE SET
          title = excluded.title,
          link = excluded.link,
          published_at_ts = excluded.published_at_ts,
          size_bytes = COALESCE(excluded.size_bytes, releases.size_bytes),
          seeders = COALESCE(excluded.seeders, releases.seeders),
          leechers = COALESCE(excluded.leechers, releases.leechers),
                    grabs = COALESCE(excluded.grabs, releases.grabs),
          info_hash = COALESCE(excluded.info_hash, releases.info_hash),
          download_url = COALESCE(excluded.download_url, releases.download_url),
          category_id = COALESCE(excluded.category_id, releases.category_id),
          std_category_id = COALESCE(excluded.std_category_id, releases.std_category_id),
          spec_category_id = COALESCE(excluded.spec_category_id, releases.spec_category_id),
          unified_category = COALESCE(excluded.unified_category, releases.unified_category),
          category_ids = COALESCE(excluded.category_ids, releases.category_ids),

          title_clean = COALESCE(excluded.title_clean, releases.title_clean),
          year = COALESCE(excluded.year, releases.year),
          season = COALESCE(excluded.season, releases.season),
          episode = COALESCE(excluded.episode, releases.episode),
          resolution = COALESCE(excluded.resolution, releases.resolution),
          source = COALESCE(excluded.source, releases.source),
          codec = COALESCE(excluded.codec, releases.codec),
          release_group = COALESCE(excluded.release_group, releases.release_group),
          media_type = COALESCE(excluded.media_type, releases.media_type);
        """;

        var rowsPayload = itemList.Select(it =>
            {
                var categoryIds = new List<int>();
                if (it.CategoryIds is { Count: > 0 })
                    categoryIds.AddRange(it.CategoryIds);
                if (it.CategoryId.HasValue)
                    categoryIds.Add(it.CategoryId.Value);

                var distinctIds = categoryIds.Distinct().ToList();
                var (stdCategoryId, specCategoryId) = UnifiedCategoryResolver.ResolveStdSpec(
                    it.StdCategoryId,
                    it.SpecCategoryId,
                    distinctIds);

                var primaryCategoryId = it.CategoryId ?? specCategoryId ?? stdCategoryId;
                var unifiedCategory = ResolveUnifiedCategoryFromMap(categoryMap, distinctIds, primaryCategoryId);
                if (unifiedCategory == UnifiedCategory.Autre)
                {
                    unifiedCategory = _resolver.Resolve(indexerKey, stdCategoryId, specCategoryId, distinctIds);
                }
                var parsed = _parser.Parse(it.Title, unifiedCategory);
                var stableGuid = BuildStableGuid(it, parsed.TitleClean);

                return new
                {
                    sourceId,
                    guid = stableGuid,
                    title = it.Title,
                    link = it.Link,
                    pub = it.PublishedAtTs,
                    size = it.SizeBytes,
                    seed = it.Seeders,
                    leech = it.Leechers,
                    grabs = it.Grabs,
                    hash = it.InfoHash,
                    dl = it.DownloadUrl,
                    cat = primaryCategoryId,
                    stdCat = stdCategoryId,
                    specCat = specCategoryId,
                    unifiedCat = unifiedCategory.ToString(),
                    catIds = distinctIds.Count > 0 ? string.Join(",", distinctIds) : null,
                    now,

                    titleClean = parsed.TitleClean,
                    year = parsed.Year,
                    season = parsed.Season,
                    episode = parsed.Episode,
                    resolution = parsed.Resolution,
                    source = parsed.Source,
                    codec = parsed.Codec,
                    group = parsed.ReleaseGroup,
                    mediaType = parsed.MediaType
                };
            })
            .ToList();

        var guidList = rowsPayload
            .Select(r => r.guid)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existing = guidList.Count == 0
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : conn.Query<string>(
                    "SELECT guid FROM releases WHERE source_id = @sid AND guid IN @guids",
                    new { sid = sourceId, guids = guidList },
                    tx)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var insertedNew = guidList.Count - existing.Count;

        conn.Execute(sql, rowsPayload, tx);

        EnsureMediaEntitiesForReleases(conn, tx, sourceId, guidList, now);

        tx.Commit();
        return insertedNew;
    }

    private static UnifiedCategory ResolveUnifiedCategoryFromMap(
        Dictionary<int, (string key, string label)>? categoryMap,
        IReadOnlyCollection<int> categoryIds,
        int? primaryCategoryId)
    {
        if (categoryMap is null || categoryMap.Count == 0) return UnifiedCategory.Autre;
        var bestId = CategorySelection.PickBestCategoryId(categoryIds, categoryMap);
        if (!bestId.HasValue && primaryCategoryId.HasValue)
            bestId = primaryCategoryId;

        if (bestId.HasValue &&
            categoryMap.TryGetValue(bestId.Value, out var entry) &&
            UnifiedCategoryMappings.TryParseKey(entry.key, out var mapped))
        {
            return mapped;
        }

        return UnifiedCategory.Autre;
    }

    private static string BuildStableGuid(TorznabItem it, string? titleClean)
    {
        var guid = it.Guid?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(guid) &&
            !string.Equals(guid, it.Title ?? "", StringComparison.OrdinalIgnoreCase))
        {
            return guid;
        }

        if (!string.IsNullOrWhiteSpace(it.InfoHash)) return it.InfoHash!;
        if (!string.IsNullOrWhiteSpace(it.DownloadUrl)) return it.DownloadUrl!;
        if (!string.IsNullOrWhiteSpace(it.Link)) return it.Link!;
        if (!string.IsNullOrWhiteSpace(guid)) return guid;

        var normalized = NormalizeTitle(titleClean ?? it.Title ?? "");
        var size = it.SizeBytes ?? 0;
        var pub = it.PublishedAtTs ?? 0;
        var rounded = pub > 0 ? (pub / 3600) * 3600 : 0;
        return $"{normalized}|{size}|{rounded}";
    }

    private static string NormalizeTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var chars = value
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray();
        return new string(chars);
    }

    private static void EnsureMediaEntitiesForReleases(
        IDbConnection conn,
        IDbTransaction tx,
        long sourceId,
        IEnumerable<string?> guids,
        long now)
    {
        var guidList = guids
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct()
            .ToArray();

        if (guidList.Length == 0) return;

        conn.Execute(
            """
            INSERT INTO media_entities(
              unified_category,
              title_clean,
              year,
              poster_file,
              poster_updated_at_ts,
              created_at_ts,
              updated_at_ts
            )
            SELECT DISTINCT
              r.unified_category,
              r.title_clean,
              r.year,
              NULL,
              NULL,
              @now,
              @now
            FROM releases r
            WHERE r.source_id = @sid
              AND r.guid IN @guids
              AND (r.entity_id IS NULL OR r.entity_id <= 0)
              AND r.unified_category IS NOT NULL
              AND r.unified_category <> ''
              AND r.title_clean IS NOT NULL
              AND r.title_clean <> ''
            ON CONFLICT DO UPDATE SET
              updated_at_ts = excluded.updated_at_ts;
            """,
            new { sid = sourceId, guids = guidList, now },
            tx
        );

        conn.Execute(
            """
            UPDATE releases
            SET entity_id = (
                SELECT me.id
                FROM media_entities me
                WHERE me.unified_category = releases.unified_category
                  AND me.title_clean = releases.title_clean
                  AND IFNULL(me.year, -1) = IFNULL(releases.year, -1)
                LIMIT 1
            )
            WHERE source_id = @sid
              AND guid IN @guids
              AND (entity_id IS NULL OR entity_id <= 0)
              AND unified_category IS NOT NULL
              AND unified_category <> ''
              AND title_clean IS NOT NULL
              AND title_clean <> '';
            """,
            new { sid = sourceId, guids = guidList },
            tx
        );
    }

    public List<long> GetNewIdsWithoutPoster(long sourceId, long createdAtTs)
    {
        using var conn = _db.Open();
        var rows = conn.Query<long>(
            """
            SELECT releases.id
            FROM releases
            LEFT JOIN media_entities me
              ON me.id = releases.entity_id
            WHERE releases.source_id = @sid
              AND releases.created_at_ts >= @ts
              AND (COALESCE(releases.poster_file, me.poster_file) IS NULL OR COALESCE(releases.poster_file, me.poster_file) = '')
            ORDER BY releases.published_at_ts DESC, releases.id DESC;
            """,
            new { sid = sourceId, ts = createdAtTs }
        );

        return rows.ToList();
    }

    public sealed class PosterJobSeed
    {
        public long Id { get; set; }
        public string? Title { get; set; }
        public string? TitleClean { get; set; }
        public int? Year { get; set; }
        public string? UnifiedCategory { get; set; }
        public long? EntityId { get; set; }
    }

    public List<PosterJobSeed> GetNewPosterJobSeeds(long sourceId, long createdAtTs)
    {
        using var conn = _db.Open();
        var rows = conn.Query<PosterJobSeed>(
            """
            SELECT
              releases.id as Id,
              releases.title as Title,
              releases.title_clean as TitleClean,
              releases.year as Year,
              releases.unified_category as UnifiedCategory,
              entity_id as EntityId
            FROM releases
            LEFT JOIN media_entities me
              ON me.id = releases.entity_id
            WHERE releases.source_id = @sid
              AND releases.created_at_ts >= @ts
              AND (COALESCE(releases.poster_file, me.poster_file) IS NULL OR COALESCE(releases.poster_file, me.poster_file) = '')
            ORDER BY releases.published_at_ts DESC, releases.id DESC;
            """,
            new { sid = sourceId, ts = createdAtTs }
        );

        return rows.AsList();
    }

    public List<long> GetIdsMissingPoster(int limit)
    {
        using var conn = _db.Open();
        var lim = Math.Clamp(limit <= 0 ? 200 : limit, 1, 1000);
        var rows = conn.Query<long>(
            """
            SELECT releases.id
            FROM releases
            LEFT JOIN media_entities me
              ON me.id = releases.entity_id
            WHERE (COALESCE(releases.poster_file, me.poster_file) IS NULL OR COALESCE(releases.poster_file, me.poster_file) = '')
            ORDER BY releases.published_at_ts DESC, releases.id DESC
            LIMIT @lim;
            """,
            new { lim }
        );

        return rows.ToList();
    }

    public int GetMissingPosterCount()
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<int>(
            """
            SELECT COUNT(1)
            FROM releases
            LEFT JOIN media_entities me
              ON me.id = releases.entity_id
            WHERE (COALESCE(releases.poster_file, me.poster_file) IS NULL OR COALESCE(releases.poster_file, me.poster_file) = '');
            """
        );
    }

    public sealed record PosterStats(int MissingTotal, int MissingActionable, long LastPosterChangeTs);

    public PosterStats GetPosterStats(long nowTs, long shortCooldownSeconds, long longCooldownSeconds)
    {
        using var conn = _db.Open();
        var shortCutoff = nowTs - shortCooldownSeconds;
        var longCutoff = nowTs - longCooldownSeconds;

        var row = conn.QueryFirstOrDefault(
            """
            SELECT
              COUNT(1) as missingTotal,
              SUM(
                CASE
                  WHEN (
                    (COALESCE(releases.poster_file, me.poster_file) IS NULL OR COALESCE(releases.poster_file, me.poster_file) = '')
                    AND (
                      poster_last_attempt_ts IS NULL
                      OR (
                        (poster_last_error IS NULL OR poster_last_error = '')
                        AND poster_last_attempt_ts <= @shortCutoff
                      )
                      OR (
                        (poster_last_error IS NOT NULL AND poster_last_error <> '')
                        AND poster_last_attempt_ts <= @longCutoff
                      )
                    )
                  )
                  THEN 1
                  ELSE 0
                END
              ) as missingActionable,
              MAX(COALESCE(releases.poster_updated_at_ts, me.poster_updated_at_ts, 0)) as lastPosterChangeTs
            FROM releases
            LEFT JOIN media_entities me
              ON me.id = releases.entity_id
            WHERE (COALESCE(releases.poster_file, me.poster_file) IS NULL OR COALESCE(releases.poster_file, me.poster_file) = '');
            """,
            new { shortCutoff, longCutoff }
        );

        var missingTotal = row is null ? 0 : Convert.ToInt32(row.missingTotal ?? 0);
        var missingActionable = row is null ? 0 : Convert.ToInt32(row.missingActionable ?? 0);
        var lastPosterChangeTs = row is null ? 0 : Convert.ToInt64(row.lastPosterChangeTs ?? 0);
        return new PosterStats(missingTotal, missingActionable, lastPosterChangeTs);
    }

    public sealed class PosterStateRow
    {
        public long Id { get; set; }
        public long? EntityId { get; set; }
        public string? PosterFile { get; set; }
        public long? PosterUpdatedAtTs { get; set; }
        public long? PosterLastAttemptTs { get; set; }
        public string? PosterLastError { get; set; }
    }

    public sealed class MissingExternalIdsSeedRow
    {
        public long ReleaseId { get; set; }
        public long? EntityId { get; set; }
        public int? TmdbId { get; set; }
        public int? TvdbId { get; set; }
    }

    public sealed class RequestTmdbSeedRow
    {
        public long ReleaseId { get; set; }
        public long? EntityId { get; set; }
        public string? TitleClean { get; set; }
        public int? Year { get; set; }
        public string? MediaType { get; set; }
        public int? RequestTmdbId { get; set; }
        public int? TmdbId { get; set; }
        public int? TvdbId { get; set; }
    }

    public List<PosterStateRow> GetPosterStateByReleaseIds(IEnumerable<long> releaseIds)
    {
        if (releaseIds is null) return new List<PosterStateRow>();
        var ids = releaseIds.Distinct().ToArray();
        if (ids.Length == 0) return new List<PosterStateRow>();

        using var conn = _db.Open();
        return conn.Query<PosterStateRow>(
            """
            SELECT
              releases.id as Id,
              releases.entity_id as EntityId,
              COALESCE(releases.poster_file, me.poster_file) as PosterFile,
              COALESCE(releases.poster_updated_at_ts, me.poster_updated_at_ts, 0) as PosterUpdatedAtTs,
              releases.poster_last_attempt_ts as PosterLastAttemptTs,
              releases.poster_last_error as PosterLastError
            FROM releases
            LEFT JOIN media_entities me
              ON me.id = releases.entity_id
            WHERE releases.id IN @ids;
            """,
            new { ids }
        ).AsList();
    }

    public List<MissingExternalIdsSeedRow> GetSeriesMissingTmdbWithTvdb(int limit)
    {
        using var conn = _db.Open();
        var lim = Math.Clamp(limit <= 0 ? 200 : limit, 1, 5000);
        return conn.Query<MissingExternalIdsSeedRow>(
            """
            SELECT
              r.id as ReleaseId,
              r.entity_id as EntityId,
              CAST(COALESCE(me.tmdb_id, r.tmdb_id) AS INTEGER) as TmdbId,
              CAST(COALESCE(me.tvdb_id, r.tvdb_id) AS INTEGER) as TvdbId
            FROM releases r
            LEFT JOIN media_entities me
              ON me.id = r.entity_id
            WHERE lower(COALESCE(r.media_type, '')) = 'series'
              AND IFNULL(COALESCE(me.tmdb_id, r.tmdb_id), 0) <= 0
              AND IFNULL(COALESCE(me.tvdb_id, r.tvdb_id), 0) > 0
            ORDER BY COALESCE(me.updated_at_ts, r.poster_updated_at_ts, r.id) DESC, r.id DESC
            LIMIT @lim;
            """,
            new { lim }
        ).AsList();
    }

    public RequestTmdbSeedRow? GetRequestTmdbSeed(long releaseId)
    {
        using var conn = _db.Open();
        return conn.QueryFirstOrDefault<RequestTmdbSeedRow>(
            """
            SELECT
              r.id as ReleaseId,
              r.entity_id as EntityId,
              r.title_clean as TitleClean,
              r.year as Year,
              lower(COALESCE(r.media_type, '')) as MediaType,
              CAST(me.request_tmdb_id AS INTEGER) as RequestTmdbId,
              CAST(COALESCE(me.tmdb_id, r.tmdb_id) AS INTEGER) as TmdbId,
              CAST(COALESCE(me.tvdb_id, r.tvdb_id) AS INTEGER) as TvdbId
            FROM releases r
            LEFT JOIN media_entities me
              ON me.id = r.entity_id
            WHERE r.id = @releaseId
            LIMIT 1;
            """,
            new { releaseId }
        );
    }

    public List<RequestTmdbSeedRow> GetSeriesMissingRequestTmdb(int limit)
    {
        using var conn = _db.Open();
        var lim = Math.Clamp(limit <= 0 ? 200 : limit, 1, 5000);
        return conn.Query<RequestTmdbSeedRow>(
            """
            SELECT
              r.id as ReleaseId,
              r.entity_id as EntityId,
              r.title_clean as TitleClean,
              r.year as Year,
              lower(COALESCE(r.media_type, '')) as MediaType,
              CAST(me.request_tmdb_id AS INTEGER) as RequestTmdbId,
              CAST(COALESCE(me.tmdb_id, r.tmdb_id) AS INTEGER) as TmdbId,
              CAST(COALESCE(me.tvdb_id, r.tvdb_id) AS INTEGER) as TvdbId
            FROM releases r
            LEFT JOIN media_entities me
              ON me.id = r.entity_id
            WHERE lower(COALESCE(r.media_type, '')) = 'series'
              AND IFNULL(me.request_tmdb_id, 0) <= 0
              AND (
                IFNULL(COALESCE(me.tmdb_id, r.tmdb_id), 0) > 0
                OR IFNULL(COALESCE(me.tvdb_id, r.tvdb_id), 0) > 0
                OR (r.title_clean IS NOT NULL AND r.title_clean <> '')
              )
            ORDER BY COALESCE(me.updated_at_ts, r.poster_updated_at_ts, r.id) DESC, r.id DESC
            LIMIT @lim;
            """,
            new { lim }
        ).AsList();
    }

    public List<MissingExternalIdsSeedRow> GetSeriesMissingTvdbWithTmdb(int limit)
    {
        using var conn = _db.Open();
        var lim = Math.Clamp(limit <= 0 ? 200 : limit, 1, 5000);
        return conn.Query<MissingExternalIdsSeedRow>(
            """
            SELECT
              r.id as ReleaseId,
              r.entity_id as EntityId,
              CAST(COALESCE(me.tmdb_id, r.tmdb_id) AS INTEGER) as TmdbId,
              CAST(COALESCE(me.tvdb_id, r.tvdb_id) AS INTEGER) as TvdbId
            FROM releases r
            LEFT JOIN media_entities me
              ON me.id = r.entity_id
            WHERE lower(COALESCE(r.media_type, '')) = 'series'
              AND IFNULL(COALESCE(me.tvdb_id, r.tvdb_id), 0) <= 0
              AND IFNULL(COALESCE(me.tmdb_id, r.tmdb_id), 0) > 0
            ORDER BY COALESCE(me.updated_at_ts, r.poster_updated_at_ts, r.id) DESC, r.id DESC
            LIMIT @lim;
            """,
            new { lim }
        ).AsList();
    }

    public int BackfillEntityPosters(int limit)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var lim = Math.Clamp(limit <= 0 ? 200 : limit, 1, 2000);

        return conn.Execute(
            """
            UPDATE media_entities
            SET poster_file = (
                  SELECT r.poster_file
                  FROM releases r
                  WHERE r.entity_id = media_entities.id
                    AND r.poster_file IS NOT NULL
                    AND r.poster_file <> ''
                  ORDER BY r.poster_updated_at_ts DESC, r.id DESC
                  LIMIT 1
              ),
                poster_updated_at_ts = (
                  SELECT r.poster_updated_at_ts
                  FROM releases r
                  WHERE r.entity_id = media_entities.id
                    AND r.poster_file IS NOT NULL
                    AND r.poster_file <> ''
                  ORDER BY r.poster_updated_at_ts DESC, r.id DESC
                  LIMIT 1
              ),
                updated_at_ts = @now
            WHERE (poster_file IS NULL OR poster_file = '')
              AND EXISTS (
                  SELECT 1
                  FROM releases r
                  WHERE r.entity_id = media_entities.id
                    AND r.poster_file IS NOT NULL
                    AND r.poster_file <> ''
              )
              AND id IN (
                  SELECT id
                  FROM media_entities
                  WHERE (poster_file IS NULL OR poster_file = '')
                  LIMIT @lim
              );
            """,
            new { now, lim }
        );
    }

    public int BackfillEntityExternalIds(int limit)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var lim = Math.Clamp(limit <= 0 ? 200 : limit, 1, 2000);

        return conn.Execute(
            """
            UPDATE media_entities
            SET tmdb_id = COALESCE(tmdb_id, (
                    SELECT r.tmdb_id
                    FROM releases r
                    WHERE r.entity_id = media_entities.id
                      AND r.tmdb_id IS NOT NULL
                      AND r.tmdb_id > 0
                    ORDER BY COALESCE(r.ext_updated_at_ts, r.poster_updated_at_ts, 0) DESC, r.id DESC
                    LIMIT 1
                )),
                tvdb_id = COALESCE(tvdb_id, (
                    SELECT r.tvdb_id
                    FROM releases r
                    WHERE r.entity_id = media_entities.id
                      AND r.tvdb_id IS NOT NULL
                      AND r.tvdb_id > 0
                    ORDER BY COALESCE(r.ext_updated_at_ts, r.poster_updated_at_ts, 0) DESC, r.id DESC
                    LIMIT 1
                )),
                updated_at_ts = @now
            WHERE (tmdb_id IS NULL OR tmdb_id <= 0 OR tvdb_id IS NULL OR tvdb_id <= 0)
              AND id IN (
                  SELECT id
                  FROM media_entities
                  WHERE (tmdb_id IS NULL OR tmdb_id <= 0 OR tvdb_id IS NULL OR tvdb_id <= 0)
                  LIMIT @lim
              )
              AND (
                  EXISTS (
                      SELECT 1
                      FROM releases r
                      WHERE r.entity_id = media_entities.id
                        AND r.tmdb_id IS NOT NULL
                        AND r.tmdb_id > 0
                  )
                  OR EXISTS (
                      SELECT 1
                      FROM releases r
                      WHERE r.entity_id = media_entities.id
                        AND r.tvdb_id IS NOT NULL
                        AND r.tvdb_id > 0
                  )
              );
            """,
            new { now, lim }
        );
    }

    public (int total, int done, int pending) GetPosterProgressByIds(IEnumerable<long> ids, long startedAtTs)
    {
        if (ids is null) return (0, 0, 0);
        var idList = ids.Distinct().ToArray();
        if (idList.Length == 0) return (0, 0, 0);

        using var conn = _db.Open();
        var row = conn.QueryFirstOrDefault(
            """
            SELECT
              COUNT(1) as total,
              SUM(
                CASE
                  WHEN (
                    (COALESCE(releases.poster_file, me.poster_file) IS NOT NULL
                     AND COALESCE(releases.poster_file, me.poster_file) <> ''
                     AND COALESCE(releases.poster_updated_at_ts, me.poster_updated_at_ts, 0) >= @startedAt)
                    OR (poster_last_attempt_ts IS NOT NULL AND poster_last_attempt_ts >= @startedAt)
                  )
                  THEN 1
                  ELSE 0
                END
              ) as done
            FROM releases
            LEFT JOIN media_entities me
              ON me.id = releases.entity_id
            WHERE releases.id IN @ids;
            """,
            new { ids = idList, startedAt = startedAtTs }
        );

        var total = row is null ? 0 : Convert.ToInt32(row.total ?? 0);
        var done = row is null ? 0 : Convert.ToInt32(row.done ?? 0);
        var pending = Math.Max(0, total - done);
        return (total, done, pending);
    }

    public string? GetDownloadUrl(long releaseId)
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<string?>(
            "SELECT download_url FROM releases WHERE id = @id",
            new { id = releaseId }
        );
    }

    public Dictionary<long, long?> GetEntityIdsForReleaseIds(IEnumerable<long> releaseIds)
    {
        if (releaseIds is null) return new Dictionary<long, long?>();
        var ids = releaseIds.Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<long, long?>();

        using var conn = _db.Open();
        var rows = conn.Query<(long ReleaseId, long? EntityId)>(
            """
            SELECT id as ReleaseId,
                   entity_id as EntityId
            FROM releases
            WHERE id IN @ids;
            """,
            new { ids }
        );

        return rows.ToDictionary(r => r.ReleaseId, r => r.EntityId);
    }

    private sealed class RenameSeedRow
    {
        public long Id { get; set; }
        public long SourceId { get; set; }
        public string? UnifiedCategory { get; set; }
        public int? CategoryId { get; set; }
        public int? StdCategoryId { get; set; }
        public int? SpecCategoryId { get; set; }
        public string? CategoryIds { get; set; }
    }

    public sealed class RenameRebindResult
    {
        public long ReleaseId { get; set; }
        public long? EntityId { get; set; }
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
        public string? UnifiedCategory { get; set; }
        public string? PosterFile { get; set; }
        public string? EntityPosterFile { get; set; }
        public long? PosterUpdatedAtTs { get; set; }
        public int? TmdbId { get; set; }
        public int? TvdbId { get; set; }
    }

    public dynamic? RenameAndRebindEntity(long id, string newTitle)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        var seed = conn.QueryFirstOrDefault<RenameSeedRow>(
            """
            SELECT
              id as Id,
              source_id as SourceId,
              unified_category as UnifiedCategory,
              category_id as CategoryId,
              std_category_id as StdCategoryId,
              spec_category_id as SpecCategoryId,
              category_ids as CategoryIds
            FROM releases
            WHERE id = @id;
            """,
            new { id },
            tx
        );

        if (seed is null) return null;

        var unifiedCategory = ResolveUnifiedCategory(seed);
        var unifiedCategoryText = unifiedCategory.ToString();
        var parsed = _parser.Parse(newTitle, unifiedCategory);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        conn.Execute(
            """
            UPDATE releases
            SET title = @title,
                title_clean = @titleClean,
                year = @year,
                season = @season,
                episode = @episode,
                resolution = @resolution,
                source = @source,
                codec = @codec,
                release_group = @releaseGroup,
                media_type = @mediaType,
                unified_category = @unifiedCategory
            WHERE id = @id;

            INSERT INTO media_entities(
              unified_category,
              title_clean,
              year,
              created_at_ts,
              updated_at_ts
            )
            VALUES(
              @unifiedCategory,
              @titleClean,
              @year,
              @now,
              @now
            )
            ON CONFLICT(unified_category, title_clean, IFNULL(year, -1)) DO UPDATE SET
              updated_at_ts = excluded.updated_at_ts;

            UPDATE releases
            SET entity_id = (
                SELECT me.id
                FROM media_entities me
                WHERE me.unified_category = @unifiedCategory
                  AND me.title_clean = @titleClean
                  AND IFNULL(me.year, -1) = IFNULL(@year, -1)
                LIMIT 1
            )
            WHERE id = @id;
            """,
            new
            {
                id,
                title = newTitle,
                titleClean = parsed.TitleClean,
                year = parsed.Year,
                season = parsed.Season,
                episode = parsed.Episode,
                resolution = parsed.Resolution,
                source = parsed.Source,
                codec = parsed.Codec,
                releaseGroup = parsed.ReleaseGroup,
                mediaType = parsed.MediaType,
                unifiedCategory = unifiedCategoryText,
                now
            },
            tx
        );

        var result = conn.QueryFirstOrDefault<RenameRebindResult>(
            """
            SELECT
              r.id as ReleaseId,
              r.entity_id as EntityId,
              r.title as Title,
              r.title_clean as TitleClean,
              r.year as Year,
              r.season as Season,
              r.episode as Episode,
              r.resolution as Resolution,
              r.source as Source,
              r.codec as Codec,
              r.release_group as ReleaseGroup,
              r.media_type as MediaType,
              r.unified_category as UnifiedCategory,
              COALESCE(r.poster_file, me.poster_file) as PosterFile,
              me.poster_file as EntityPosterFile,
              COALESCE(r.poster_updated_at_ts, me.poster_updated_at_ts, 0) as PosterUpdatedAtTs,
              CAST(COALESCE(me.tmdb_id, r.tmdb_id) AS INTEGER) as TmdbId,
              CAST(COALESCE(me.tvdb_id, r.tvdb_id) AS INTEGER) as TvdbId
            FROM releases r
            LEFT JOIN media_entities me
              ON me.id = r.entity_id
            WHERE r.id = @id;
            """,
            new { id },
            tx
        );

        tx.Commit();
        return result;
    }

    private UnifiedCategory ResolveUnifiedCategory(RenameSeedRow seed)
    {
        if (UnifiedCategoryMappings.TryParse(seed.UnifiedCategory, out var unifiedCategory) &&
            unifiedCategory != UnifiedCategory.Autre)
        {
            return unifiedCategory;
        }

        var categoryIds = ParseCategoryIds(seed.CategoryIds, seed.CategoryId);
        var (stdId, specId) = UnifiedCategoryResolver.ResolveStdSpec(seed.StdCategoryId, seed.SpecCategoryId, categoryIds);
        return _resolver.Resolve(null, stdId, specId, categoryIds);
    }

    private static List<int> ParseCategoryIds(string? csv, int? primaryId)
    {
        var ids = new List<int>();
        if (primaryId.HasValue) ids.Add(primaryId.Value);
        if (!string.IsNullOrWhiteSpace(csv))
        {
            foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part, out var id))
                    ids.Add(id);
            }
        }

        return ids.Distinct().ToList();
    }
    public ReleaseForPoster? GetForPoster(long id)
    {
        using var conn = _db.Open();
        return conn.QueryFirstOrDefault<ReleaseForPoster>(
            """
            SELECT
                releases.id as Id,
                releases.source_id as SourceId,
                releases.entity_id as EntityId,
                releases.title as Title,
                releases.title_clean as TitleClean,
                releases.year as Year,
                season as Season,
                episode as Episode,
                category_id as CategoryId,
                sc.name as CategoryName,
                std_category_id as StdCategoryId,
                spec_category_id as SpecCategoryId,
                releases.unified_category as UnifiedCategory,
                category_ids as CategoryIds,
                media_type as MediaType,
                COALESCE(me.tmdb_id, releases.tmdb_id) as TmdbId,
                COALESCE(me.tvdb_id, releases.tvdb_id) as TvdbId,
                poster_path as PosterPath,
                COALESCE(releases.poster_file, me.poster_file) as PosterFile,
                poster_provider as PosterProvider,
                poster_provider_id as PosterProviderId,
                poster_lang as PosterLang,
                poster_size as PosterSize,
                poster_hash as PosterHash,
                COALESCE(releases.poster_updated_at_ts, me.poster_updated_at_ts, 0) as PosterUpdatedAtTs,
                poster_last_attempt_ts as PosterLastAttemptTs,
                poster_last_error as PosterLastError,
                COALESCE(me.ext_provider, releases.ext_provider) as ExtProvider,
                COALESCE(me.ext_provider_id, releases.ext_provider_id) as ExtProviderId,
                COALESCE(me.ext_title, releases.ext_title) as ExtTitle,
                COALESCE(me.ext_overview, releases.ext_overview) as ExtOverview,
                COALESCE(me.ext_tagline, releases.ext_tagline) as ExtTagline,
                COALESCE(me.ext_genres, releases.ext_genres) as ExtGenres,
                COALESCE(me.ext_release_date, releases.ext_release_date) as ExtReleaseDate,
                COALESCE(me.ext_runtime_minutes, releases.ext_runtime_minutes) as ExtRuntimeMinutes,
                COALESCE(me.ext_rating, releases.ext_rating) as ExtRating,
                COALESCE(me.ext_votes, releases.ext_votes) as ExtVotes,
                COALESCE(me.ext_updated_at_ts, releases.ext_updated_at_ts) as ExtUpdatedAtTs,
                COALESCE(me.ext_directors, releases.ext_directors) as ExtDirectors,
                COALESCE(me.ext_writers, releases.ext_writers) as ExtWriters,
                COALESCE(me.ext_cast, releases.ext_cast) as ExtCast
            FROM releases
            LEFT JOIN media_entities me
              ON me.id = releases.entity_id
            LEFT JOIN source_categories sc
              ON sc.source_id = releases.source_id AND sc.cat_id = releases.category_id
            WHERE releases.id = @id;
            """,
            new { id }
        );
    }


    public void SavePoster(long id, int? tmdbId, string? posterPath, string posterFile)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var entityId = conn.ExecuteScalar<long?>(
            "SELECT entity_id FROM releases WHERE id = @id",
            new { id }
        );

        if (entityId.HasValue && entityId.Value > 0)
        {
            conn.Execute(
                """
                UPDATE media_entities
                SET tmdb_id = COALESCE(@tmdbId, tmdb_id),
                    poster_file = @posterFile,
                    poster_updated_at_ts = @ts,
                    updated_at_ts = @ts
                WHERE id = @entityId;
                """,
                new { entityId, tmdbId, posterFile, ts = now }
            );
        }

        conn.Execute(
            """
            UPDATE releases
            SET tmdb_id = COALESCE(@tmdbId, tmdb_id),
                poster_path = COALESCE(@posterPath, poster_path),
                poster_file = @posterFile,
                poster_updated_at_ts = @ts
            WHERE id = @id;
            """,
            new { id, tmdbId, posterPath, posterFile, ts = now }
        );
    }

    public PosterMatch? GetPosterForTitleClean(long excludeId, string titleClean, string? normalizedTitle, string? mediaType, int? year)
    {
        using var conn = _db.Open();

        const string selectColumns = @"
SELECT r.id as Id,
       r.entity_id as EntityId,
       COALESCE(me.tmdb_id, r.tmdb_id) as TmdbId,
       COALESCE(me.tvdb_id, r.tvdb_id) as TvdbId,
       r.poster_path as PosterPath,
       COALESCE(r.poster_file, me.poster_file) as PosterFile,
       r.poster_provider as PosterProvider,
       r.poster_provider_id as PosterProviderId,
       r.poster_lang as PosterLang,
       r.poster_size as PosterSize,
       r.poster_hash as PosterHash,
       COALESCE(me.ext_provider, r.ext_provider) as ExtProvider,
       COALESCE(me.ext_provider_id, r.ext_provider_id) as ExtProviderId,
       COALESCE(me.ext_title, r.ext_title) as ExtTitle,
       COALESCE(me.ext_overview, r.ext_overview) as ExtOverview,
       COALESCE(me.ext_tagline, r.ext_tagline) as ExtTagline,
       COALESCE(me.ext_genres, r.ext_genres) as ExtGenres,
       COALESCE(me.ext_release_date, r.ext_release_date) as ExtReleaseDate,
       COALESCE(me.ext_runtime_minutes, r.ext_runtime_minutes) as ExtRuntimeMinutes,
       COALESCE(me.ext_rating, r.ext_rating) as ExtRating,
       COALESCE(me.ext_votes, r.ext_votes) as ExtVotes,
       COALESCE(me.ext_updated_at_ts, r.ext_updated_at_ts) as ExtUpdatedAtTs,
       COALESCE(me.ext_directors, r.ext_directors) as ExtDirectors,
       COALESCE(me.ext_writers, r.ext_writers) as ExtWriters,
       COALESCE(me.ext_cast, r.ext_cast) as ExtCast
FROM releases r
LEFT JOIN media_entities me
  ON me.id = r.entity_id
";

        // 1. Essai exact: même titre + même année + même type
        var result = conn.QueryFirstOrDefault<PosterMatch>(
            selectColumns + """
            WHERE r.id <> @id
              AND r.title_clean IS NOT NULL
              AND (
                    lower(r.title_clean) = lower(@title)
                    OR (@altTitle IS NOT NULL AND lower(r.title_clean) = lower(@altTitle))
                  )
              AND (@mediaType IS NULL OR lower(r.media_type) = lower(@mediaType))
              AND (@year IS NULL OR r.year = @year)
              AND COALESCE(r.poster_file, me.poster_file) IS NOT NULL
              AND COALESCE(r.poster_file, me.poster_file) <> ''
            ORDER BY COALESCE(r.poster_updated_at_ts, me.poster_updated_at_ts) DESC, r.id DESC
            LIMIT 1;
            """,
            new { id = excludeId, title = titleClean, altTitle = normalizedTitle, mediaType, year }
        );

        if (result is not null) return result;

        // 2. Fallback: tolérance ±1 an sur l'année
        if (year.HasValue)
        {
            result = conn.QueryFirstOrDefault<PosterMatch>(
                selectColumns + """
                WHERE r.id <> @id
                  AND r.title_clean IS NOT NULL
                  AND (
                        lower(r.title_clean) = lower(@title)
                        OR (@altTitle IS NOT NULL AND lower(r.title_clean) = lower(@altTitle))
                      )
                  AND (@mediaType IS NULL OR lower(r.media_type) = lower(@mediaType))
                  AND r.year BETWEEN @yearMin AND @yearMax
                  AND COALESCE(r.poster_file, me.poster_file) IS NOT NULL
                  AND COALESCE(r.poster_file, me.poster_file) <> ''
                ORDER BY ABS(r.year - @year), COALESCE(r.poster_updated_at_ts, me.poster_updated_at_ts) DESC, r.id DESC
                LIMIT 1;
                """,
                new { id = excludeId, title = titleClean, altTitle = normalizedTitle, mediaType, year, yearMin = year - 1, yearMax = year + 1 }
            );

            if (result is not null) return result;
        }

        // 3. Fallback: même titre sans contrainte d'année (si année fournie)
        if (year.HasValue)
        {
            result = conn.QueryFirstOrDefault<PosterMatch>(
                selectColumns + """
                WHERE r.id <> @id
                  AND r.title_clean IS NOT NULL
                  AND (
                        lower(r.title_clean) = lower(@title)
                        OR (@altTitle IS NOT NULL AND lower(r.title_clean) = lower(@altTitle))
                      )
                  AND (@mediaType IS NULL OR lower(r.media_type) = lower(@mediaType))
                  AND COALESCE(r.poster_file, me.poster_file) IS NOT NULL
                  AND COALESCE(r.poster_file, me.poster_file) <> ''
                ORDER BY COALESCE(r.poster_updated_at_ts, me.poster_updated_at_ts) DESC, r.id DESC
                LIMIT 1;
                """,
                new { id = excludeId, title = titleClean, altTitle = normalizedTitle, mediaType }
            );
        }

        return result;
    }

    public void SaveTmdbId(long id, int tmdbId)
    {
        if (tmdbId <= 0) return;

        using var conn = _db.Open();
        var entityId = conn.ExecuteScalar<long?>(
            "SELECT entity_id FROM releases WHERE id = @id",
            new { id }
        );

        if (entityId.HasValue && entityId.Value > 0)
        {
            conn.Execute(
                """
                UPDATE media_entities
                SET tmdb_id = COALESCE(@tmdbId, tmdb_id),
                    updated_at_ts = @ts
                WHERE id = @entityId;
                """,
                new { entityId, tmdbId, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
            );
        }

        conn.Execute(
            """
            UPDATE releases
            SET tmdb_id = COALESCE(@tmdbId, tmdb_id)
            WHERE id = @id;
            """,
            new { id, tmdbId }
        );
    }

    public void SaveTvdbId(long id, int tvdbId)
    {
        using var conn = _db.Open();
        var entityId = conn.ExecuteScalar<long?>(
            "SELECT entity_id FROM releases WHERE id = @id",
            new { id }
        );

        if (entityId.HasValue && entityId.Value > 0)
        {
            conn.Execute(
                """
                UPDATE media_entities
                SET tvdb_id = COALESCE(@tvdbId, tvdb_id),
                    updated_at_ts = @ts
                WHERE id = @entityId;
                """,
                new { entityId, tvdbId, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
            );
        }

        conn.Execute(
            "UPDATE releases SET tvdb_id = @tvdbId WHERE id = @id;",
            new { id, tvdbId }
        );
    }

    public void SaveRequestTmdbResolution(long releaseId, int? requestTmdbId, string status)
    {
        if (releaseId <= 0) return;

        using var conn = _db.Open();
        var entityId = conn.ExecuteScalar<long?>(
            "SELECT entity_id FROM releases WHERE id = @releaseId",
            new { releaseId }
        );

        if (!entityId.HasValue || entityId.Value <= 0)
            return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cleanStatus = string.IsNullOrWhiteSpace(status) ? "unknown" : status.Trim().ToLowerInvariant();
        var value = requestTmdbId.HasValue && requestTmdbId.Value > 0 ? requestTmdbId.Value : (int?)null;

        conn.Execute(
            """
            UPDATE media_entities
            SET request_tmdb_id = COALESCE(@requestTmdbId, request_tmdb_id),
                request_tmdb_status = @status,
                request_tmdb_updated_at_ts = @now,
                updated_at_ts = @now
            WHERE id = @entityId;
            """,
            new
            {
                entityId,
                requestTmdbId = value,
                status = cleanStatus,
                now
            }
        );
    }

    public void UpsertArrStatus(IEnumerable<ReleaseArrStatusRow> rows)
    {
        if (rows is null) return;
        var list = rows.ToList();
        if (list.Count == 0) return;

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        conn.Execute(
            """
            INSERT INTO release_arr_status (
              release_id,
              in_sonarr,
              in_radarr,
              sonarr_url,
              radarr_url,
              checked_at_ts
            )
            VALUES (
              @ReleaseId,
              @InSonarr,
              @InRadarr,
              @SonarrUrl,
              @RadarrUrl,
              @CheckedAtTs
            )
            ON CONFLICT(release_id) DO UPDATE SET
              in_sonarr = excluded.in_sonarr,
              in_radarr = excluded.in_radarr,
              sonarr_url = excluded.sonarr_url,
              radarr_url = excluded.radarr_url,
              checked_at_ts = excluded.checked_at_ts;
            """,
            list,
            tx
        );

        tx.Commit();
    }

    public void UpdatePosterAttemptSuccess(
        long id,
        string? provider,
        string? providerId,
        string? lang,
        string? size,
        string? hash)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        conn.Execute(
            """
            UPDATE releases
            SET poster_provider = @provider,
                poster_provider_id = @providerId,
                poster_lang = @lang,
                poster_size = @size,
                poster_hash = @hash,
                poster_last_attempt_ts = @ts,
                poster_last_error = NULL
            WHERE id = @id;
            """,
            new { id, provider, providerId, lang, size, hash, ts = now }
        );
    }

    public void UpdatePosterAttemptFailure(
        long id,
        string? provider,
        string? providerId,
        string? lang,
        string? size,
        string? error)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        conn.Execute(
            """
            UPDATE releases
            SET poster_provider = COALESCE(@provider, poster_provider),
                poster_provider_id = COALESCE(@providerId, poster_provider_id),
                poster_lang = COALESCE(@lang, poster_lang),
                poster_size = COALESCE(@size, poster_size),
                poster_last_attempt_ts = @ts,
                poster_last_error = @error
            WHERE id = @id;
            """,
            new { id, provider, providerId, lang, size, error, ts = now }
        );
    }

    public void UpdateExternalDetails(
        long id,
        string provider,
        string providerId,
        string? title,
        string? overview,
        string? tagline,
        string? genres,
        string? releaseDate,
        int? runtimeMinutes,
        double? rating,
        int? votes,
        string? directors,
        string? writers,
        string? cast)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var entityId = conn.ExecuteScalar<long?>(
            "SELECT entity_id FROM releases WHERE id = @id",
            new { id }
        );

        if (entityId.HasValue && entityId.Value > 0)
        {
            conn.Execute(
                """
                UPDATE media_entities
                SET ext_provider = @provider,
                    ext_provider_id = @providerId,
                    ext_title = COALESCE(@title, ext_title),
                    ext_overview = COALESCE(@overview, ext_overview),
                    ext_tagline = COALESCE(@tagline, ext_tagline),
                    ext_genres = COALESCE(@genres, ext_genres),
                    ext_release_date = COALESCE(@releaseDate, ext_release_date),
                    ext_runtime_minutes = COALESCE(@runtimeMinutes, ext_runtime_minutes),
                    ext_rating = COALESCE(@rating, ext_rating),
                    ext_votes = COALESCE(@votes, ext_votes),
                    ext_directors = COALESCE(@directors, ext_directors),
                    ext_writers = COALESCE(@writers, ext_writers),
                    ext_cast = COALESCE(@cast, ext_cast),
                    ext_updated_at_ts = @ts,
                    updated_at_ts = @ts
                WHERE id = @entityId;
                """,
                new
                {
                    entityId,
                    provider,
                    providerId,
                    title,
                    overview,
                    tagline,
                    genres,
                    releaseDate,
                    runtimeMinutes,
                    rating,
                    votes,
                    directors,
                    writers,
                    cast,
                    ts = now
                }
            );
        }

        conn.Execute(
            """
            UPDATE releases
            SET ext_provider = @provider,
                ext_provider_id = @providerId,
                ext_title = COALESCE(@title, ext_title),
                ext_overview = COALESCE(@overview, ext_overview),
                ext_tagline = COALESCE(@tagline, ext_tagline),
                ext_genres = COALESCE(@genres, ext_genres),
                ext_release_date = COALESCE(@releaseDate, ext_release_date),
                ext_runtime_minutes = COALESCE(@runtimeMinutes, ext_runtime_minutes),
                ext_rating = COALESCE(@rating, ext_rating),
                ext_votes = COALESCE(@votes, ext_votes),
                ext_directors = COALESCE(@directors, ext_directors),
                ext_writers = COALESCE(@writers, ext_writers),
                ext_cast = COALESCE(@cast, ext_cast),
                ext_updated_at_ts = @ts
            WHERE id = @id;
            """,
            new
            {
                id,
                provider,
                providerId,
                title,
                overview,
                tagline,
                genres,
                releaseDate,
                runtimeMinutes,
                rating,
                votes,
                directors,
                writers,
                cast,
                ts = now
            }
        );
    }

    public sealed record RetentionResult(
        int TotalBefore,
        int TotalAfter,
        int PurgedByPerCategory,
        int PurgedByGlobal,
        Dictionary<string, int> PerKeyBefore,
        Dictionary<string, int> PerKeyAfter,
        List<long> DeletedReleaseIds,
        List<string> PosterFiles);

    public RetentionResult ApplyRetention(long sourceId, int perCatLimit, int globalLimit)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        var perKeyBefore = GetPerKeyCounts(conn, tx, sourceId);
        var totalBefore = perKeyBefore.Values.Sum();

        var deletedIds = new List<long>();
        var posterFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var purgedPerCat = 0;
        if (perCatLimit > 0)
        {
            var perCatIds = conn.Query<long>(
                """
                WITH ranked AS (
                  SELECT id,
                         unified_category,
                         ROW_NUMBER() OVER (
                           PARTITION BY unified_category
                           ORDER BY COALESCE(published_at_ts, created_at_ts) DESC, id DESC
                         ) AS rn
                  FROM releases
                  WHERE source_id = @sid
                    AND unified_category IN @cats
                )
                SELECT id FROM ranked WHERE rn > @limit;
                """,
                new { sid = sourceId, cats = RetentionUnifiedCategories, limit = perCatLimit },
                tx
            ).AsList();

            if (perCatIds.Count > 0)
            {
                foreach (var file in GetPosterFilesForReleaseIds(conn, tx, perCatIds))
                    posterFiles.Add(file);

                DeleteReleaseArrStatus(conn, tx, perCatIds);
                DeleteReleases(conn, tx, perCatIds);

                deletedIds.AddRange(perCatIds);
                purgedPerCat = perCatIds.Count;
            }
        }

        var purgedGlobal = 0;
        if (globalLimit > 0)
        {
            var globalIds = conn.Query<long>(
                """
                WITH ranked AS (
                  SELECT id,
                         ROW_NUMBER() OVER (
                           ORDER BY COALESCE(published_at_ts, created_at_ts) DESC, id DESC
                         ) AS rn
                  FROM releases
                  WHERE source_id = @sid
                )
                SELECT id FROM ranked WHERE rn > @limit;
                """,
                new { sid = sourceId, limit = globalLimit },
                tx
            ).AsList();

            if (globalIds.Count > 0)
            {
                foreach (var file in GetPosterFilesForReleaseIds(conn, tx, globalIds))
                    posterFiles.Add(file);

                DeleteReleaseArrStatus(conn, tx, globalIds);
                DeleteReleases(conn, tx, globalIds);

                deletedIds.AddRange(globalIds);
                purgedGlobal = globalIds.Count;
            }
        }

        var perKeyAfter = GetPerKeyCounts(conn, tx, sourceId);
        var totalAfter = perKeyAfter.Values.Sum();

        tx.Commit();

        return new RetentionResult(
            totalBefore,
            totalAfter,
            purgedPerCat,
            purgedGlobal,
            perKeyBefore,
            perKeyAfter,
            deletedIds,
            posterFiles.ToList());
    }

    public int GetPosterReferenceCount(string posterFile)
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<int>(
            """
            SELECT COUNT(1)
            FROM releases r
            LEFT JOIN media_entities me
              ON me.id = r.entity_id
            WHERE r.poster_file = @file
               OR me.poster_file = @file;
            """,
            new { file = posterFile }
        );
    }

    public HashSet<string> GetReferencedPosterFiles(IEnumerable<string> posterFiles)
    {
        if (posterFiles is null)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var files = posterFiles
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var conn = _db.Open();
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const int chunkSize = 400;

        for (var index = 0; index < files.Count; index += chunkSize)
        {
            var chunk = files.Skip(index).Take(chunkSize).ToArray();
            var rows = conn.Query<string>(
                """
                SELECT DISTINCT poster_file
                FROM (
                    SELECT r.poster_file as poster_file
                    FROM releases r
                    WHERE r.poster_file IN @files

                    UNION ALL

                    SELECT me.poster_file as poster_file
                    FROM media_entities me
                    WHERE me.poster_file IN @files
                ) refs;
                """,
                new { files = chunk }
            );

            foreach (var file in rows)
            {
                if (!string.IsNullOrWhiteSpace(file))
                    referenced.Add(file);
            }
        }

        return referenced;
    }

    public HashSet<string> GetReferencedPosterFiles()
    {
        using var conn = _db.Open();
        var rows = conn.Query<string>(
            """
            SELECT DISTINCT poster_file
            FROM (
                SELECT r.poster_file as poster_file
                FROM releases r
                WHERE r.poster_file IS NOT NULL AND r.poster_file <> ''

                UNION ALL

                SELECT me.poster_file as poster_file
                FROM media_entities me
                WHERE me.poster_file IS NOT NULL AND me.poster_file <> ''
            ) refs;
            """
        );

        return rows
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public void ClearPosterFileReferences(string posterFile)
    {
        if (string.IsNullOrWhiteSpace(posterFile))
            return;

        using var conn = _db.Open();
        var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        conn.Execute(
            """
            UPDATE releases
            SET poster_file = NULL,
                poster_updated_at_ts = NULL
            WHERE poster_file = @file;
            """,
            new { file = posterFile }
        );

        conn.Execute(
            """
            UPDATE media_entities
            SET poster_file = NULL,
                poster_updated_at_ts = NULL,
                updated_at_ts = @now
            WHERE poster_file = @file;
            """,
            new { file = posterFile, now = nowTs }
        );

        conn.Execute(
            "DELETE FROM poster_matches WHERE poster_file = @file;",
            new { file = posterFile }
        );
    }

    public void ClearPosterFileReferences(IEnumerable<string> posterFiles)
    {
        var files = posterFiles
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
            return;

        using var conn = _db.Open();
        var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        conn.Execute(
            """
            UPDATE releases
            SET poster_file = NULL,
                poster_updated_at_ts = NULL
            WHERE poster_file IN @files;
            """,
            new { files }
        );

        conn.Execute(
            """
            UPDATE media_entities
            SET poster_file = NULL,
                poster_updated_at_ts = NULL,
                updated_at_ts = @now
            WHERE poster_file IN @files;
            """,
            new { files, now = nowTs }
        );

        conn.Execute(
            "DELETE FROM poster_matches WHERE poster_file IN @files;",
            new { files }
        );
    }

    public void ClearAllPosterReferences()
    {
        using var conn = _db.Open();
        var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        conn.Execute(
            """
            UPDATE releases
            SET poster_file = NULL,
                poster_updated_at_ts = NULL
            WHERE poster_file IS NOT NULL
              AND poster_file <> '';
            """
        );

        conn.Execute(
            """
            UPDATE media_entities
            SET poster_file = NULL,
                poster_updated_at_ts = NULL,
                updated_at_ts = @now
            WHERE poster_file IS NOT NULL
              AND poster_file <> '';
            """,
            new { now = nowTs }
        );

        conn.Execute("DELETE FROM poster_matches;");
    }

    private static Dictionary<string, int> GetPerKeyCounts(IDbConnection conn, IDbTransaction tx, long sourceId)
    {
        var rows = conn.Query(
            """
            SELECT unified_category as unifiedCategory, COUNT(1) as count
            FROM releases
            WHERE source_id = @sid
            GROUP BY unified_category;
            """,
            new { sid = sourceId },
            tx
        );

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var unifiedValue = (string?)row.unifiedCategory;
            if (UnifiedCategoryMappings.TryParse(unifiedValue, out var unifiedCategory) &&
                unifiedCategory != UnifiedCategory.Autre)
            {
                var key = UnifiedCategoryMappings.ToKey(unifiedCategory);
                result[key] = result.TryGetValue(key, out var current) ? current + Convert.ToInt32(row.count ?? 0) : Convert.ToInt32(row.count ?? 0);
            }
            else
            {
                result["other"] = result.TryGetValue("other", out var current) ? current + Convert.ToInt32(row.count ?? 0) : Convert.ToInt32(row.count ?? 0);
            }
        }

        return result;
    }

    private static void DeleteReleases(IDbConnection conn, IDbTransaction tx, IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0) return;
        conn.Execute("DELETE FROM releases WHERE id IN @ids;", new { ids }, tx);
    }

    private static void DeleteReleaseArrStatus(IDbConnection conn, IDbTransaction tx, IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0) return;
        conn.Execute("DELETE FROM release_arr_status WHERE release_id IN @ids;", new { ids }, tx);
    }

    private static IEnumerable<string> GetPosterFilesForReleaseIds(IDbConnection conn, IDbTransaction tx, IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0) return Array.Empty<string>();
        var rows = conn.Query<string>(
            """
            SELECT DISTINCT COALESCE(r.poster_file, me.poster_file) as posterFile
            FROM releases r
            LEFT JOIN media_entities me
              ON me.id = r.entity_id
            WHERE r.id IN @ids
              AND COALESCE(r.poster_file, me.poster_file) IS NOT NULL
              AND COALESCE(r.poster_file, me.poster_file) <> '';
            """,
            new { ids },
            tx
        );
        return rows.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
    }

    public int ReparseAllTitles(int batchSize = 1000)
    {
        using var conn = _db.Open();
        var limit = Math.Clamp(batchSize <= 0 ? 1000 : batchSize, 1, 5000);
        var updated = 0;
        long lastId = 0;

        while (true)
        {
            var rows = conn.Query<(long id, string title, string? unifiedCategory)>(
                """
                SELECT id, title, unified_category as unifiedCategory
                FROM releases
                WHERE id > @lastId
                ORDER BY id
                LIMIT @limit;
                """,
                new { limit, lastId }
            ).AsList();

            if (rows.Count == 0) break;

            using var tx = conn.BeginTransaction();
            foreach (var row in rows)
            {
                var category = UnifiedCategory.Autre;
                if (!string.IsNullOrWhiteSpace(row.unifiedCategory))
                    UnifiedCategoryMappings.TryParse(row.unifiedCategory, out category);

                var parsed = _parser.Parse(row.title ?? "", category);

                conn.Execute(
                    """
                    UPDATE releases SET
                        title_clean = @titleClean,
                        year = @year,
                        season = @season,
                        episode = @episode,
                        resolution = @resolution,
                        source = @source,
                        codec = @codec,
                        release_group = @group,
                        media_type = @mediaType
                    WHERE id = @id;
                    """,
                    new
                    {
                        id = row.id,
                        titleClean = parsed.TitleClean,
                        year = parsed.Year,
                        season = parsed.Season,
                        episode = parsed.Episode,
                        resolution = parsed.Resolution,
                        source = parsed.Source,
                        codec = parsed.Codec,
                        group = parsed.ReleaseGroup,
                        mediaType = parsed.MediaType
                    },
                    tx
                );
                updated++;
            }
            tx.Commit();
            lastId = rows[^1].id;
        }

        return updated;
    }

    public sealed record DuplicateResult(int GroupsFound, int DuplicatesCount, List<long> DuplicateIds);

    public DuplicateResult DetectDuplicates()
    {
        using var conn = _db.Open();
        var duplicateIds = conn.Query<long>(
            """
            WITH ranked AS (
                SELECT id,
                       ROW_NUMBER() OVER (
                           PARTITION BY source_id, title_clean
                           ORDER BY COALESCE(published_at_ts, created_at_ts) DESC, id DESC
                       ) AS rn
                FROM releases
                WHERE title_clean IS NOT NULL AND title_clean <> ''
            )
            SELECT id FROM ranked WHERE rn > 1;
            """
        ).AsList();

        var groupsFound = conn.ExecuteScalar<int>(
            """
            SELECT COUNT(1) FROM (
                SELECT 1
                FROM releases
                WHERE title_clean IS NOT NULL AND title_clean <> ''
                GROUP BY source_id, title_clean
                HAVING COUNT(1) > 1
            );
            """
        );

        return new DuplicateResult(groupsFound, duplicateIds.Count, duplicateIds);
    }

    public int PurgeDuplicates(List<long> ids)
    {
        if (ids.Count == 0) return 0;
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        conn.Execute("DELETE FROM release_arr_status WHERE release_id IN @ids;", new { ids }, tx);
        var deleted = conn.Execute("DELETE FROM releases WHERE id IN @ids;", new { ids }, tx);

        tx.Commit();
        return deleted;
    }

}

public sealed class ReleaseArrStatusRow
{
    public long ReleaseId { get; set; }
    public bool InSonarr { get; set; }
    public bool InRadarr { get; set; }
    public string? SonarrUrl { get; set; }
    public string? RadarrUrl { get; set; }
    public long CheckedAtTs { get; set; }
}
