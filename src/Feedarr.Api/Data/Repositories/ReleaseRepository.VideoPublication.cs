using Dapper;
using Feedarr.Api.Services.Tmdb;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Categories;

namespace Feedarr.Api.Data.Repositories;

public sealed partial class ReleaseRepository
{
    internal int? FindVerifiedRssTmdbIdentity(string provider, string externalId, string type)
    {
        if (provider is not ("imdb" or "tvdb") || type is not ("movie" or "tv")) return null;
        using var conn = _db.Open();
        var ids = conn.Query<int>("""
            SELECT DISTINCT tmdb_id FROM media_entities
            WHERE ext_snapshot_version=1 AND ext_provider='tmdb'
              AND ext_provider_id=CAST(tmdb_id AS TEXT) AND tmdb_id>0 AND tmdb_media_type=@type
              AND ((@provider='imdb' AND imdb_id=@externalId) OR (@provider='tvdb' AND tvdb_id=@externalId))
            LIMIT 2
            """, new { provider, externalId, type }).ToArray();
        return ids.Length == 1 ? ids[0] : null;
    }

    // Shared matching publication boundary, using the historical writers' exact SQL/helpers.
    // The complete image already exists; no network or filesystem IO inside this transaction.
    internal void PublishAcceptedVideo(long id, string provider, string providerId, int? tmdbId, string? type,
        string posterPath, string posterFile, TmdbClient.DetailsResult details, AnimeEvidence? anime = null, bool refreshExisting = false, ReleaseForPoster? sharedPoster = null)
    {
        if (provider is "jikan" or "igdb" or "rawg" or "theaudiodb" or "musicbrainz" or "googlebooks" or "openlibrary" or "comicvine" ? type is not null || tmdbId.HasValue : type is not ("movie" or "tv") || provider is not ("tmdb" or "tvmaze")) throw new ArgumentException(nameof(type));
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction(deferred: false);
        ValidateMatching(conn, tx, id, tmdbId, provider: provider, providerId: providerId, tmdbType: type);
        if (refreshExisting)
        {
            var known = GetForPublicationIdentity(conn, tx, id);
            var manual = known.ManualMatchProvider == provider && known.ManualMatchId == providerId;
            var automatic = known.ExtProvider == provider && known.ExtProviderId == providerId;
            var tmdb = provider == "tmdb" && known.TmdbId == tmdbId && known.TmdbMediaType == type &&
                (known.ExtProvider is null || known.ExtProvider == "tmdb" && known.ExtProviderId == providerId);
            if (!manual && (known.TmdbId is > 0 && provider != "tmdb" || known.TvdbId is > 0 && provider is not ("tmdb" or "tvmaze"))) throw new StaleMatchException();
            if (!(manual || automatic || tmdb) || (provider == "tmdb" && known.TmdbMediaType != type)) throw new StaleMatchException();
        }
        if (conn.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM releases r LEFT JOIN media_entities me ON me.id=r.entity_id
            WHERE r.id=@id AND NOT @refreshExisting AND (COALESCE(me.tmdb_id,r.tmdb_id,0)>0 OR COALESCE(me.tvdb_id,r.tvdb_id,0)>0
              OR COALESCE(me.manual_match_provider,'')<>'' OR r.title_manual_override=1
              OR (COALESCE(me.ext_provider_id,r.ext_provider_id,'')<>'' AND NOT (
                @refreshable AND COALESCE(me.ext_provider,r.ext_provider,'')=@provider
                AND COALESCE(me.ext_provider_id,r.ext_provider_id,'')=@providerId)))
            """, new { id, provider, providerId, refreshExisting, refreshable = provider is "theaudiodb" or "musicbrainz" or "googlebooks" or "openlibrary" or "comicvine" }, tx) != 0) throw new StaleMatchException();
        if (provider == "jikan" && !refreshExisting) ValidateJikanEntityScope(conn, tx, id, providerId, anime);
        if (!refreshExisting && provider is ("igdb" or "rawg"))
        {
            // Game editions must not leak through a shared entity to another release scope.
            var rows = conn.Query<JikanScopeRow>("""
                SELECT id Id,title Title,unified_category Category,COALESCE(title_manual_override,0) ManualTitle
                FROM releases WHERE id=@id OR entity_id=(SELECT entity_id FROM releases WHERE id=@id) ORDER BY id LIMIT 101
                """, new { id }, tx).ToArray();
            var rawTitle = rows.Single(r => r.Id == id).Title ?? "";
            var context = GameCandidateAdapter.Context(rawTitle);
            var distributionScope = GameSearchScope.Read(rawTitle) with { BareEdition = null };
            if (rows.Length > 100 || rows.Any(row => row.ManualTitle ||
                !(UnifiedCategoryMappings.TryParse(row.Category, out var category) && category == UnifiedCategory.JeuWindows) ||
                GameCandidateAdapter.Context(row.Title ?? "") is var other && (other.GameEdition != context.GameEdition ||
                    (GameSearchScope.Read(row.Title ?? "") with { BareEdition = null }) !=
                    distributionScope ||
                    !other.Queries.Select(q => q with { Title = TitleNormalizer.NormalizeTitleStrict(q.Title) })
                        .SequenceEqual(context.Queries.Select(q => q with { Title = TitleNormalizer.NormalizeTitleStrict(q.Title) })))))
                throw new InvalidDataException("game_shared_entity_scope_unverified");
        }
        if (!refreshExisting && provider is ("theaudiodb" or "musicbrainz"))
        {
            var rows = conn.Query<JikanScopeRow>("""
                SELECT id Id,title Title,unified_category Category,COALESCE(title_manual_override,0) ManualTitle
                FROM releases WHERE id=@id OR entity_id=(SELECT entity_id FROM releases WHERE id=@id) ORDER BY id LIMIT 101
                """, new { id }, tx).ToArray();
            var scope = AudioCandidateAdapter.Scope(rows.Single(r => r.Id == id).Title ?? "");
            if (rows.Length > 100 || rows.Any(row => row.ManualTitle ||
                !UnifiedCategoryMappings.TryParse(row.Category, out var category) || category != UnifiedCategory.Audio ||
                AudioCandidateAdapter.Scope(row.Title ?? "") != scope)) throw new InvalidDataException("audio_shared_entity_scope_unverified");
        }
        if (!refreshExisting && provider is ("googlebooks" or "openlibrary" or "comicvine"))
        {
            var category = provider == "comicvine" ? UnifiedCategory.Comic : UnifiedCategory.Book;
            var rows = conn.Query<JikanScopeRow>("""
                SELECT id Id,title Title,unified_category Category,COALESCE(title_manual_override,0) ManualTitle
                FROM releases WHERE id=@id OR entity_id=(SELECT entity_id FROM releases WHERE id=@id) ORDER BY id LIMIT 101
                """, new { id }, tx).ToArray();
            var scope = PrintCandidateAdapter.Scope(rows.Single(row => row.Id == id).Title ?? "", category);
            if (rows.Length > 100 || rows.Any(row => row.ManualTitle ||
                !UnifiedCategoryMappings.TryParse(row.Category, out var parsed) || parsed != category ||
                PrintCandidateAdapter.Scope(row.Title ?? "", category) != scope)) throw new InvalidDataException("print_shared_entity_scope_unverified");
        }
        SavePosterCore(conn, tx, id, tmdbId, posterPath, posterFile, type, provider, providerId);
        // Lock is held and identity just validated/written by this transaction; avoid treating our own write as a stale worker.
        UpdateExternalDetailsCore(conn, tx, id, provider, providerId,
            details.Title, details.Overview, details.Tagline, details.Genres, details.ReleaseDate, details.RuntimeMinutes,
            details.Rating, details.Votes, details.Directors, details.Writers, details.Cast, type, validate: false);
        if (sharedPoster is not null)
        {
            if (sharedPoster.ExtSnapshotVersion != 1 || sharedPoster.ExtProvider != provider || sharedPoster.ExtProviderId != providerId ||
                sharedPoster.PosterProvider != provider || sharedPoster.PosterProviderId != providerId ||
                (provider == "tmdb" && (sharedPoster.TmdbId != tmdbId || sharedPoster.TmdbMediaType != type)) ||
                string.IsNullOrWhiteSpace(sharedPoster.PosterStoreDir) || string.IsNullOrWhiteSpace(sharedPoster.PosterHash))
                throw new StaleMatchException();
            // Identity, metadata, poster and GC reference become visible in one commit.
            conn.Execute("""
                UPDATE media_entities SET poster_key=@key,poster_store_dir=@store
                WHERE id=(SELECT entity_id FROM releases WHERE id=@id);
                UPDATE releases SET poster_key=@key,poster_store_dir=@store,poster_hash=@hash,
                    poster_provider=@provider,poster_provider_id=@providerId,poster_lang=@lang,poster_size=@size,
                    poster_last_attempt_ts=@now,poster_last_error=NULL WHERE id=@id;
                DELETE FROM poster_store_refs WHERE release_id=@id AND store_dir<>@store;
                INSERT OR IGNORE INTO poster_store_refs(store_dir,release_id,created_at_ts) VALUES(@store,@id,@now);
                """, new { id, key=sharedPoster.PosterKey, store=sharedPoster.PosterStoreDir, hash=sharedPoster.PosterHash,
                    provider, providerId, lang=sharedPoster.PosterLang, size=sharedPoster.PosterSize, now=DateTimeOffset.UtcNow.ToUnixTimeSeconds() }, tx);
        }
        AdvanceMatchingSnapshot(conn, tx, id);
        tx.Commit();
    }

    internal IReadOnlyList<long> GetPosterStoreDonors(string storeDir)
    {
        using var conn = _db.Open();
        return conn.Query<long>("SELECT release_id FROM poster_store_refs WHERE store_dir=@storeDir ORDER BY created_at_ts DESC LIMIT 8", new { storeDir }).ToArray();
    }

    private static ReleaseForPoster GetForPublicationIdentity(System.Data.IDbConnection conn, System.Data.IDbTransaction tx, long id) =>
        conn.QuerySingle<ReleaseForPoster>("""
            SELECT me.manual_match_provider ManualMatchProvider,me.manual_match_id ManualMatchId,
                COALESCE(me.ext_provider,r.ext_provider) ExtProvider,COALESCE(me.ext_provider_id,r.ext_provider_id) ExtProviderId,
                COALESCE(me.tmdb_id,r.tmdb_id) TmdbId,COALESCE(me.tvdb_id,r.tvdb_id) TvdbId,COALESCE(me.tmdb_media_type,r.tmdb_media_type) TmdbMediaType
            FROM releases r LEFT JOIN media_entities me ON me.id=r.entity_id WHERE r.id=@id
            """, new { id }, tx);

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
