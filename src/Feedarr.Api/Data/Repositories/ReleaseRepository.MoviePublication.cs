using Dapper;
using System.Text.Json;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Data.Repositories;

public sealed partial class ReleaseRepository
{
    private sealed record MovieV2Cohort(long ConfiguredMinimum, long EffectiveMinimum);

    internal long InitializeMovieV2Cohort(long configuredMinimum)
    {
        if (configuredMinimum is <= 0 or long.MaxValue) throw new InvalidOperationException("matching_v2_rollout_invalid");
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction(deferred: false);
        const string key = "matching_v2_movies_cohort";
        var json = conn.ExecuteScalar<string?>("SELECT value_json FROM app_settings WHERE key=@key", new { key }, tx);
        if (json is not null)
        {
            MovieV2Cohort? existing;
            try { existing = JsonSerializer.Deserialize<MovieV2Cohort>(json); }
            catch (JsonException) { throw new InvalidOperationException("matching_v2_rollout_marker_invalid"); }
            if (existing is null || existing.ConfiguredMinimum != configuredMinimum || existing.EffectiveMinimum < configuredMinimum)
                throw new InvalidOperationException("matching_v2_rollout_marker_mismatch");
            // Never widen or reset the cohort after a restart/configuration change.
            return existing.EffectiveMinimum;
        }
        var maximum = conn.ExecuteScalar<long>("SELECT COALESCE(MAX(id),0) FROM releases", transaction: tx);
        var effective = Math.Max(configuredMinimum, maximum < long.MaxValue ? maximum + 1 : long.MaxValue);
        conn.Execute("INSERT INTO app_settings(key,value_json,updated_at_ts) VALUES(@key,@json,unixepoch())",
            new { key, json = JsonSerializer.Serialize(new MovieV2Cohort(configuredMinimum, effective)) }, tx);
        tx.Commit();
        return effective;
    }

    // Narrow active-movie publication boundary, using the historical writers' exact SQL/helpers.
    // The complete image already exists; no network or filesystem IO inside this transaction.
    internal void PublishAcceptedMovie(long id, int tmdbId, string posterPath, string posterFile, TmdbClient.DetailsResult details)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction(deferred: false);
        ValidateMatching(conn, tx, id, tmdbId, tmdbType: "movie");
        if (conn.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM releases r LEFT JOIN media_entities me ON me.id=r.entity_id
            WHERE r.id=@id AND (COALESCE(me.tmdb_id,r.tmdb_id,0)>0 OR COALESCE(me.tvdb_id,r.tvdb_id,0)>0
              OR COALESCE(me.manual_match_provider,'')<>'' OR r.title_manual_override=1
              OR COALESCE(me.ext_provider_id,r.ext_provider_id,'')<>'')
            """, new { id }, tx) != 0) throw new StaleMatchException();
        SavePosterCore(conn, tx, id, tmdbId, posterPath, posterFile, "movie");
        // Lock is held and identity just validated/written by this transaction; avoid treating our own write as a stale worker.
        UpdateExternalDetailsCore(conn, tx, id, "tmdb", tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            details.Title, details.Overview, details.Tagline, details.Genres, details.ReleaseDate, details.RuntimeMinutes,
            details.Rating, details.Votes, details.Directors, details.Writers, details.Cast, "movie", validate: false);
        AdvanceMatchingSnapshot(conn, tx, id);
        tx.Commit();
    }
}
