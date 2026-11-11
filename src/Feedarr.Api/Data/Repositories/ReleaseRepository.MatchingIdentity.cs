using System.Data;
using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Data.Repositories;

public sealed partial class ReleaseRepository
{
    private readonly AsyncLocal<ReleaseForPoster?> _matchingSnapshot = new();
    internal sealed class StaleMatchException() : InvalidOperationException("Matching identity changed; stale result discarded.");
    private sealed class MatchingScope(Action dispose) : IDisposable { public void Dispose() => dispose(); }

    internal IDisposable BeginMatching(ReleaseForPoster snapshot)
    {
        var previous = _matchingSnapshot.Value;
        _matchingSnapshot.Value = snapshot;
        return new MatchingScope(() => _matchingSnapshot.Value = previous);
    }

    private void ValidateMatching(IDbConnection conn, IDbTransaction tx, long id,
        int? tmdbId = null, string? provider = null, string? providerId = null, string? tmdbType = null)
    {
        var current = conn.QueryFirstOrDefault<ReleaseForPoster>("""
            SELECT r.entity_id EntityId, me.match_revision MatchRevision,
                   me.manual_match_provider ManualMatchProvider, me.manual_match_id ManualMatchId,
                   me.manual_match_type ManualMatchType
            FROM releases r LEFT JOIN media_entities me ON me.id=r.entity_id WHERE r.id=@id
            """, new { id }, tx);
        var expected = _matchingSnapshot.Value;
        if (current is null || (expected is not null &&
            (current.EntityId != expected.EntityId || current.MatchRevision != expected.MatchRevision)))
            throw new StaleMatchException();
        if (current.EntityId.HasValue && conn.ExecuteScalar<int>("""
            SELECT COUNT(DISTINCT CASE
              WHEN COALESCE(me.tmdb_media_type,r.tmdb_media_type) IN ('movie','tv') THEN COALESCE(me.tmdb_media_type,r.tmdb_media_type)
              WHEN lower(r.media_type) IN ('series','tv','show') THEN 'tv'
              WHEN lower(r.media_type) IN ('movie','film') THEN 'movie'
              ELSE 'unknown' END)
            FROM releases r JOIN media_entities me ON me.id=r.entity_id WHERE me.id=@entity
            """, new { entity = current.EntityId }, tx) > 1)
            throw new StaleMatchException(); // Preserve historically mixed/ambiguous entity associations.
        if (current.ManualMatchProvider is not null &&
            ((tmdbId.HasValue && (current.ManualMatchProvider != "tmdb" || current.ManualMatchId != tmdbId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))) ||
             (tmdbType is not null && current.ManualMatchProvider == "tmdb" && TmdbIdentity.NormalizeType(current.ManualMatchType) != tmdbType) ||
             (provider is not null && (provider != current.ManualMatchProvider || providerId != current.ManualMatchId))))
            throw new StaleMatchException();
    }

    internal void ValidateMatching(long id)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        ValidateMatching(conn, tx, id);
    }

    /// <summary>One user choice applies atomically to all releases sharing the entity.</summary>
    internal void SetManualMatch(long id, string provider, string mediaType, string externalId)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction(deferred: false);
        var entity = conn.ExecuteScalar<long?>("SELECT entity_id FROM releases WHERE id=@id", new { id }, tx);
        if (entity is null)
        {
            conn.Execute("""
                INSERT INTO media_entities(unified_category,title_clean,year,created_at_ts,updated_at_ts,retention_origin)
                SELECT unified_category,title_clean,year,unixepoch(),unixepoch(),'manual' FROM releases WHERE id=@id
                ON CONFLICT DO NOTHING;
                UPDATE releases SET entity_id=(SELECT me.id FROM media_entities me
                  WHERE me.unified_category=releases.unified_category AND me.title_clean=releases.title_clean
                    AND IFNULL(me.year,-1)=IFNULL(releases.year,-1)) WHERE id=@id;
                """, new { id }, tx);
            entity = conn.ExecuteScalar<long?>("SELECT entity_id FROM releases WHERE id=@id", new { id }, tx);
            if (entity is null) throw new InvalidOperationException("A linked entity is required for a manual match.");
        }
        var revision = Guid.NewGuid().ToString("N");
        var tmdbType = provider == "tmdb" ? TmdbIdentity.NormalizeType(mediaType) : null;
        if (provider == "tmdb" && (tmdbType is null || !int.TryParse(externalId, out var number) || number <= 0))
            throw new ArgumentException("A typed positive TMDB identity is required.");
        InvalidateMatchingTitles(conn, tx, entity.Value);
        conn.Execute("""
            UPDATE media_entities SET manual_match_provider=@provider, manual_match_type=@mediaType,
              manual_match_id=@externalId, match_revision=@revision, retention_origin='manual',
              tmdb_id=CASE WHEN @provider='tmdb' THEN CAST(@externalId AS INTEGER) END, tmdb_media_type=@tmdbType,
              tvdb_id=NULL, request_tmdb_id=NULL, request_tmdb_status=NULL, request_tmdb_updated_at_ts=NULL,
              poster_file=NULL, poster_key=NULL, poster_store_dir=NULL, poster_updated_at_ts=NULL,
              ext_provider=NULL, ext_provider_id=NULL, ext_title=NULL, ext_overview=NULL, ext_tagline=NULL,
              ext_genres=NULL, ext_release_date=NULL, ext_runtime_minutes=NULL, ext_rating=NULL, ext_votes=NULL,
              ext_directors=NULL, ext_writers=NULL, ext_cast=NULL, ext_updated_at_ts=NULL
            WHERE id=@entity;
            UPDATE releases SET tmdb_id=CASE WHEN @provider='tmdb' THEN CAST(@externalId AS INTEGER) END,
              tmdb_media_type=@tmdbType,
              media_type=CASE WHEN @tmdbType='tv' THEN 'series' WHEN @tmdbType='movie' THEN 'movie' ELSE media_type END,
              tvdb_id=NULL, poster_file=NULL, poster_path=NULL,
              poster_key=NULL, poster_store_dir=NULL, poster_updated_at_ts=NULL,
              poster_provider=NULL, poster_provider_id=NULL, poster_hash=NULL,
              ext_provider=NULL, ext_provider_id=NULL, ext_title=NULL, ext_overview=NULL, ext_tagline=NULL,
              ext_genres=NULL, ext_release_date=NULL, ext_runtime_minutes=NULL, ext_rating=NULL, ext_votes=NULL,
              ext_directors=NULL, ext_writers=NULL, ext_cast=NULL, ext_updated_at_ts=NULL
            WHERE entity_id=@entity;
            DELETE FROM media_entity_arr_status WHERE entity_id=@entity;
            """, new { entity, provider, mediaType, externalId, revision, tmdbType }, tx);
        tx.Commit();
    }

    private static void InvalidateMatchingTitles(IDbConnection conn, IDbTransaction tx, long entity)
    {
        var releases = conn.Query<ReleaseForPoster>("""
            SELECT r.title_clean TitleClean, r.media_type MediaType, r.year Year, r.unified_category UnifiedCategory,
              COALESCE(me.tmdb_media_type,r.tmdb_media_type) TmdbMediaType
            FROM releases r JOIN media_entities me ON me.id=r.entity_id WHERE me.id=@entity
            """, new { entity }, tx);
        foreach (var release in releases)
        {
            var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { release.MediaType ?? "unknown" };
            if (UnifiedCategoryMappings.TryParse(release.UnifiedCategory, out var category))
                types.Add(UnifiedCategoryMappings.ToMediaType(category));
            if (TmdbIdentity.NormalizeType(release.TmdbMediaType ?? release.MediaType) is { } type)
                foreach (var alias in type == "tv" ? new[] { "tv", "series", "show" } : new[] { "movie", "film" }) types.Add(alias);
            conn.Execute("""
                DELETE FROM poster_matches WHERE normalized_title=@title AND lower(media_type) IN @types
                  AND (year IS NULL OR @year IS NULL OR year=@year)
                """, new { title = TitleNormalizer.NormalizeTitle(release.TitleClean ?? ""), types = types.Select(value => value.ToLowerInvariant()).ToArray(), year = release.Year }, tx);
        }
    }

    public bool ClearManualMatch(long id)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction(deferred: false);
        var entity = conn.ExecuteScalar<long?>("SELECT entity_id FROM releases WHERE id=@id", new { id }, tx);
        if (entity is null) return false;
        InvalidateMatchingTitles(conn, tx, entity.Value);
        conn.Execute("""
            UPDATE media_entities SET manual_match_provider=NULL, manual_match_type=NULL, manual_match_id=NULL,
              match_revision=@revision WHERE id=@entity;
            """, new { entity, revision = Guid.NewGuid().ToString("N") }, tx);
        tx.Commit();
        return true;
    }
}
