using Dapper;

namespace Feedarr.Api.Data.Repositories;

public sealed class MediaEntityArrStatusRepository
{
    private readonly Db _db;

    public MediaEntityArrStatusRepository(Db db)
    {
        _db = db;
    }

    public List<MediaEntityArrStatusRow> GetByEntityIds(IEnumerable<long> entityIds)
    {
        if (entityIds is null) return new List<MediaEntityArrStatusRow>();
        var ids = entityIds.Distinct().ToArray();
        if (ids.Length == 0) return new List<MediaEntityArrStatusRow>();

        using var conn = _db.Open();
        return conn.Query<MediaEntityArrStatusRow>(
            """
            SELECT
              entity_id as EntityId,
              in_sonarr as InSonarr,
              in_radarr as InRadarr,
              sonarr_url as SonarrUrl,
              radarr_url as RadarrUrl,
              checked_at_ts as CheckedAtTs,
              match_method as MatchMethod,
              sonarr_item_id as SonarrItemId,
              radarr_item_id as RadarrItemId
            FROM media_entity_arr_status
            WHERE entity_id IN @ids;
            """,
            new { ids }
        ).AsList();
    }

    public void UpsertMany(IEnumerable<MediaEntityArrStatusRow> rows)
    {
        if (rows is null) return;
        var list = rows.ToList();
        if (list.Count == 0) return;

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        conn.Execute(
            """
            INSERT INTO media_entity_arr_status (
              entity_id,
              in_sonarr,
              in_radarr,
              sonarr_url,
              radarr_url,
              checked_at_ts,
              match_method,
              sonarr_item_id,
              radarr_item_id
            )
            VALUES (
              @EntityId,
              @InSonarr,
              @InRadarr,
              @SonarrUrl,
              @RadarrUrl,
              @CheckedAtTs,
              @MatchMethod,
              @SonarrItemId,
              @RadarrItemId
            )
            ON CONFLICT(entity_id) DO UPDATE SET
              in_sonarr = excluded.in_sonarr,
              in_radarr = excluded.in_radarr,
              sonarr_url = excluded.sonarr_url,
              radarr_url = excluded.radarr_url,
              checked_at_ts = excluded.checked_at_ts,
              match_method = excluded.match_method,
              sonarr_item_id = excluded.sonarr_item_id,
              radarr_item_id = excluded.radarr_item_id;
            """,
            list,
            tx
        );

        tx.Commit();
    }

    public List<long> GetEntityIdsNeedingRefresh(long minCheckedAtTs, int limit)
    {
        var lim = Math.Clamp(limit <= 0 ? 500 : limit, 1, 2000);
        using var conn = _db.Open();
        return conn.Query<long>(
            """
            SELECT me.id
            FROM media_entities me
            LEFT JOIN media_entity_arr_status s
              ON s.entity_id = me.id
            WHERE s.entity_id IS NULL
               OR s.checked_at_ts < @minTs
            ORDER BY me.updated_at_ts DESC, me.id DESC
            LIMIT @lim;
            """,
            new { minTs = minCheckedAtTs, lim }
        ).ToList();
    }
}

public sealed class MediaEntityArrStatusRow
{
    public long EntityId { get; set; }
    public bool InSonarr { get; set; }
    public bool InRadarr { get; set; }
    public string? SonarrUrl { get; set; }
    public string? RadarrUrl { get; set; }
    public long CheckedAtTs { get; set; }
    public string? MatchMethod { get; set; }
    public int? SonarrItemId { get; set; }
    public int? RadarrItemId { get; set; }
}
