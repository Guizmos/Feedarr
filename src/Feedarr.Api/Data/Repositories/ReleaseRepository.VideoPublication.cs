using Dapper;
using Feedarr.Api.Services.Tmdb;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Categories;

namespace Feedarr.Api.Data.Repositories;

public sealed partial class ReleaseRepository
{
    // Shared video publication boundary, using the historical writers' exact SQL/helpers.
    // The complete image already exists; no network or filesystem IO inside this transaction.
    internal void PublishAcceptedVideo(long id, string provider, string providerId, int? tmdbId, string? type,
        string posterPath, string posterFile, TmdbClient.DetailsResult details, AnimeEvidence? anime = null)
    {
        if (provider == "jikan" ? type is not null || tmdbId.HasValue : type is not ("movie" or "tv") || provider is not ("tmdb" or "tvmaze")) throw new ArgumentException(nameof(type));
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction(deferred: false);
        ValidateMatching(conn, tx, id, tmdbId, provider: provider, providerId: providerId, tmdbType: type);
        if (conn.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM releases r LEFT JOIN media_entities me ON me.id=r.entity_id
            WHERE r.id=@id AND (COALESCE(me.tmdb_id,r.tmdb_id,0)>0 OR COALESCE(me.tvdb_id,r.tvdb_id,0)>0
              OR COALESCE(me.manual_match_provider,'')<>'' OR r.title_manual_override=1
              OR COALESCE(me.ext_provider_id,r.ext_provider_id,'')<>'')
            """, new { id }, tx) != 0) throw new StaleMatchException();
        if (provider == "jikan") ValidateJikanEntityScope(conn, tx, id, providerId, anime);
        SavePosterCore(conn, tx, id, tmdbId, posterPath, posterFile, type, provider, providerId);
        // Lock is held and identity just validated/written by this transaction; avoid treating our own write as a stale worker.
        UpdateExternalDetailsCore(conn, tx, id, provider, providerId,
            details.Title, details.Overview, details.Tagline, details.Genres, details.ReleaseDate, details.RuntimeMinutes,
            details.Rating, details.Votes, details.Directors, details.Writers, details.Cast, type, validate: false);
        AdvanceMatchingSnapshot(conn, tx, id);
        tx.Commit();
    }

    private static void ValidateJikanEntityScope(System.Data.IDbConnection conn, System.Data.IDbTransaction tx,
        long id, string providerId, AnimeEvidence? evidence)
    {
        if (evidence?.Identity != new SearchIdentity("jikan", SearchMediaType.Anime, providerId))
            throw new InvalidDataException("jikan_publication_identity_unavailable");
        var rows = conn.Query<JikanScopeRow>("""
            SELECT r.id Id,r.title Title,r.unified_category Category,COALESCE(r.title_manual_override,0) ManualTitle
            FROM releases r WHERE r.id=@id OR r.entity_id=(SELECT entity_id FROM releases WHERE id=@id)
            ORDER BY r.id LIMIT 101
            """, new { id }, tx).ToArray();
        if (rows.Length > 100) throw new InvalidDataException("jikan_shared_entity_scope_unverified");
        var target = rows.Single(r => r.Id == id);
        var context = JikanCandidateAdapter.Context(target.Title ?? "");
        foreach (var row in rows.Where(r => r.Id != id))
        {
            var other = JikanCandidateAdapter.Context(row.Title ?? "");
            var anime = (UnifiedCategoryMappings.TryParse(row.Category, out var category) || UnifiedCategoryMappings.TryParseKey(row.Category, out category)) && category == UnifiedCategory.Anime;
            // A MAL entry is a season/edition, unlike a TMDB TV identity covering an entire series.
            // Do not propagate it across incompatible release scopes through the shared entity.
            if (!anime || row.ManualTitle || !context.Queries.Select(q => q with { Title = TitleNormalizer.NormalizeTitleStrict(q.Title) })
                    .SequenceEqual(other.Queries.Select(q => q with { Title = TitleNormalizer.NormalizeTitleStrict(q.Title) })) ||
                context.Season != other.Season || context.AnimeFormat != other.AnimeFormat ||
                (context.Episode != other.Episode && (evidence.Episodes is not > 0 ||
                    context.Episode is null || other.Episode is null || other.Episode > evidence.Episodes)))
                throw new InvalidDataException("jikan_shared_entity_scope_unverified");
        }
    }
    private sealed class JikanScopeRow
    {
        public long Id { get; set; }
        public string? Title { get; set; }
        public string? Category { get; set; }
        public bool ManualTitle { get; set; }
    }

}
