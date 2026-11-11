using System.Data;
using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Data.Repositories;

public sealed partial class ReleaseRepository
{
    // A provider snapshot is replaceable data, not user content. Never transfer a
    // missing field from another work, including historical hybrid snapshots.
    private void PrepareExternalIdentity(IDbConnection conn, IDbTransaction tx, long id,
        string provider, string providerId, string? tmdbType, int? mappedTmdbId, bool publishingPoster)
    {
        var current = conn.QuerySingle<ReleaseForPoster>("""
            SELECT r.entity_id EntityId, COALESCE(me.tmdb_id,r.tmdb_id) TmdbId, COALESCE(me.tvdb_id,r.tvdb_id) TvdbId,
              COALESCE(me.tmdb_media_type,r.tmdb_media_type) TmdbMediaType, r.media_type MediaType,
              CASE WHEN me.ext_provider IS NOT NULL THEN me.ext_snapshot_version ELSE r.ext_snapshot_version END ExtSnapshotVersion,
              COALESCE(me.ext_provider,r.ext_provider) ExtProvider, COALESCE(me.ext_provider_id,r.ext_provider_id) ExtProviderId,
              r.poster_provider PosterProvider, r.poster_provider_id PosterProviderId,
              COALESCE(r.poster_file,me.poster_file) PosterFile, COALESCE(r.poster_key,me.poster_key) PosterKey
            FROM releases r LEFT JOIN media_entities me ON me.id=r.entity_id WHERE r.id=@id
            """, new { id }, tx);
        var type = TmdbIdentity.NormalizeType(tmdbType ?? current.TmdbMediaType ?? current.MediaType);
        if (provider == "tmdb" && type is null) throw new StaleMatchException(); // Do not guess a historical namespace.
        var oldType = TmdbIdentity.NormalizeType(current.TmdbMediaType ?? current.MediaType);
        var sameMappedTmdb = mappedTmdbId.HasValue && mappedTmdbId == current.TmdbId && type == oldType;
        var samePoster = PosterBelongsToIdentity(current, provider, providerId, type);
        if (publishingPoster && (provider is "tmdb" or "tvmaze") && sameMappedTmdb &&
            current.ExtProvider == "tmdb" && current.ExtProviderId == mappedTmdbId?.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
            current.ExtSnapshotVersion == 1) return; // New image, same verified work and metadata owner.
        var identityChanged = (publishingPoster && current.ExtProvider is null && !samePoster) ||
            (provider == "tvmaze" && current.TmdbId.HasValue && !mappedTmdbId.HasValue) ||
            (current.ExtProvider is not null &&
                (current.ExtProvider != provider || current.ExtProviderId != providerId)) ||
            (provider == "tmdb" && !sameMappedTmdb) ||
            (provider is not ("tmdb" or "tvmaze") && (current.TmdbId.HasValue || current.TvdbId.HasValue)) ||
            (publishingPoster && current.PosterProvider is not null &&
                (current.PosterProvider != provider || current.PosterProviderId != providerId));
        // A mapped TVMaze/Fanart poster can coexist with TMDB details for the same work.
        var mappedPoster = provider == "tmdb" && sameMappedTmdb &&
            (current.PosterProvider is "tvmaze" or "fanart" || current.PosterFile?.StartsWith($"fanart-{type}-{providerId}", StringComparison.Ordinal) == true);
        if (!identityChanged) return;
        if (current.ExtProvider is not null || current.TmdbId.HasValue || current.PosterFile is not null)
            InvalidateMatchingTitles(conn, tx, current.EntityId ?? -1, id);
        var entity = current.EntityId;
        var clearPoster = publishingPoster || (!samePoster && !mappedPoster);
        var clearIds = (!mappedPoster && (publishingPoster || !samePoster ||
            current.ExtProvider != provider || current.ExtProviderId != providerId ||
            (provider == "tmdb" && !sameMappedTmdb))) ||
            (provider is not ("tmdb" or "tvmaze") && (current.TmdbId.HasValue || current.TvdbId.HasValue));
        var newTmdbId = provider == "tmdb" ? int.Parse(providerId, System.Globalization.CultureInfo.InvariantCulture)
            : provider == "tvmaze" ? mappedTmdbId : null;
        // Keep user titles, seen state, release fields and manual-match provenance untouched.
        foreach (var table in new[] { "media_entities", "releases" })
        {
            var where = table == "media_entities" ? "id=@entity" : "id=@id OR (@entity IS NOT NULL AND entity_id=@entity)";
            conn.Execute($"""
                UPDATE {table} SET
                  ext_snapshot_version=NULL, ext_provider=NULL, ext_provider_id=NULL, ext_title=NULL, ext_overview=NULL, ext_tagline=NULL,
                  ext_genres=NULL, ext_release_date=NULL, ext_runtime_minutes=NULL, ext_rating=NULL, ext_votes=NULL,
                  ext_directors=NULL, ext_writers=NULL, ext_cast=NULL, ext_updated_at_ts=NULL,
                  tmdb_id=CASE WHEN @clearIds THEN @newTmdbId ELSE tmdb_id END,
                  tmdb_media_type=CASE WHEN @clearIds THEN @newType ELSE tmdb_media_type END,
                  tvdb_id=CASE WHEN @clearIds THEN NULL ELSE tvdb_id END,
                  poster_file=CASE WHEN @clearPoster THEN NULL ELSE poster_file END,
                  poster_key=CASE WHEN @clearPoster THEN NULL ELSE poster_key END,
                  poster_store_dir=CASE WHEN @clearPoster THEN NULL ELSE poster_store_dir END,
                  poster_updated_at_ts=CASE WHEN @clearPoster THEN NULL ELSE poster_updated_at_ts END
                WHERE {where}
                """, new { id, entity, clearIds, newTmdbId, newType = newTmdbId.HasValue ? type : null, clearPoster }, tx);
        }
        if (clearIds)
            conn.Execute("""
                UPDATE media_entities SET imdb_id=NULL, request_tmdb_id=NULL, request_tmdb_status=NULL,
                  request_tmdb_updated_at_ts=NULL WHERE id=@entity;
                DELETE FROM media_entity_arr_status WHERE entity_id=@entity;
                """, new { entity }, tx);
        if (clearPoster)
            conn.Execute("""
                UPDATE releases SET poster_path=NULL,poster_provider=NULL,poster_provider_id=NULL,
                  poster_hash=NULL,poster_lang=NULL,poster_size=NULL
                WHERE id=@id OR (@entity IS NOT NULL AND entity_id=@entity)
                """, new { id, entity }, tx);
    }

    private static bool PosterBelongsToIdentity(ReleaseForPoster row, string provider, string providerId, string? type)
    {
        if (row.PosterFile is null) return false;
        var key = provider == "tmdb" ? $"tmdb:{type}:{providerId}" : $"{provider}:{providerId}";
        if (row.PosterKey == key) return true;
        if (provider == "tmdb") return row.PosterFile.StartsWith($"tmdb-{type}-{providerId}-", StringComparison.OrdinalIgnoreCase);
        return row.PosterProvider == provider && row.PosterProviderId == providerId;
    }
}
