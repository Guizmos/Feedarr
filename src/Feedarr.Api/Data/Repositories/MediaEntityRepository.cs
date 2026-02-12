using Dapper;

namespace Feedarr.Api.Data.Repositories;

public sealed class MediaEntityRepository
{
    private readonly Db _db;

    public MediaEntityRepository(Db db)
    {
        _db = db;
    }

    public List<MediaEntityInfo> GetByIds(IEnumerable<long> entityIds)
    {
        if (entityIds is null) return new List<MediaEntityInfo>();
        var ids = entityIds.Distinct().ToArray();
        if (ids.Length == 0) return new List<MediaEntityInfo>();

        using var conn = _db.Open();
        return conn.Query<MediaEntityInfo>(
            """
            SELECT
              id as Id,
              unified_category as UnifiedCategory,
              title_clean as TitleClean,
              year as Year,
              tmdb_id as TmdbId,
              tvdb_id as TvdbId
            FROM media_entities
            WHERE id IN @ids;
            """,
            new { ids }
        ).AsList();
    }

    public MediaEntityPoster? GetPoster(long entityId)
    {
        using var conn = _db.Open();
        return conn.QueryFirstOrDefault<MediaEntityPoster>(
            """
            SELECT
              id as Id,
              poster_file as PosterFile,
              poster_updated_at_ts as PosterUpdatedAtTs
            FROM media_entities
            WHERE id = @id;
            """,
            new { id = entityId }
        );
    }
}

public sealed class MediaEntityInfo
{
    public long Id { get; set; }
    public string? UnifiedCategory { get; set; }
    public string? TitleClean { get; set; }
    public int? Year { get; set; }
    public int? TmdbId { get; set; }
    public int? TvdbId { get; set; }
}

public sealed class MediaEntityPoster
{
    public long Id { get; set; }
    public string? PosterFile { get; set; }
    public long? PosterUpdatedAtTs { get; set; }
}
