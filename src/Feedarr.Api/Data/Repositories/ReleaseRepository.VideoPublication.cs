using Dapper;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Data.Repositories;

public sealed partial class ReleaseRepository
{
    // Shared video publication boundary, using the historical writers' exact SQL/helpers.
    // The complete image already exists; no network or filesystem IO inside this transaction.
    internal void PublishAcceptedVideo(long id, string provider, string providerId, int? tmdbId, string type,
        string posterPath, string posterFile, TmdbClient.DetailsResult details)
    {
        if (type is not ("movie" or "tv") || provider is not ("tmdb" or "tvmaze")) throw new ArgumentException(nameof(type));
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction(deferred: false);
        ValidateMatching(conn, tx, id, tmdbId, provider: provider, providerId: providerId, tmdbType: type);
        if (conn.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM releases r LEFT JOIN media_entities me ON me.id=r.entity_id
            WHERE r.id=@id AND (COALESCE(me.tmdb_id,r.tmdb_id,0)>0 OR COALESCE(me.tvdb_id,r.tvdb_id,0)>0
              OR COALESCE(me.manual_match_provider,'')<>'' OR r.title_manual_override=1
              OR COALESCE(me.ext_provider_id,r.ext_provider_id,'')<>'')
            """, new { id }, tx) != 0) throw new StaleMatchException();
        SavePosterCore(conn, tx, id, tmdbId, posterPath, posterFile, type, provider, providerId);
        // Lock is held and identity just validated/written by this transaction; avoid treating our own write as a stale worker.
        UpdateExternalDetailsCore(conn, tx, id, provider, providerId,
            details.Title, details.Overview, details.Tagline, details.Genres, details.ReleaseDate, details.RuntimeMinutes,
            details.Rating, details.Votes, details.Directors, details.Writers, details.Cast, type, validate: false);
        AdvanceMatchingSnapshot(conn, tx, id);
        tx.Commit();
    }
}
