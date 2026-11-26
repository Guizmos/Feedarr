using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Data.Repositories;

public sealed partial class ReleaseRepository
{
    internal sealed record IdentityReuseResult(bool Reused, string Reason, long? DonorId = null,
        long? SourceId = null, string? Provider = null, string? ProviderId = null) { internal bool ImageReusable { get; init; } }

    internal IdentityReuseResult TryReuseLocalIdentity(long id, ReleaseForPoster? validatedDonor = null, Feedarr.Api.Services.Tmdb.TmdbClient.DetailsResult? validatedDetails = null)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction(deferred: false);
        ValidateMatching(conn, tx, id);
        var targetState = MatchingReadOnly.ReadState(conn, id);
        if (targetState is null || targetState.Protected) return new(false, "target_protected");
        var target = GetForReuse(id);
        var hash = conn.ExecuteScalar<string?>("SELECT lower(info_hash) FROM releases WHERE id=@id", new { id }, tx);
        var validHash = hash is not null && System.Text.RegularExpressions.Regex.IsMatch(hash, "^[a-f0-9]{40}$|^[a-f0-9]{64}$");
        // No enabled-source filter: disabling synchronization does not invalidate evidence.
        var candidates = conn.Query<long>("""
            SELECT id FROM releases WHERE id<>@id AND
              (title=@title COLLATE NOCASE OR (@validHash AND info_hash IS NOT NULL AND lower(info_hash)=@hash))
            ORDER BY id LIMIT 33
            """, new { id, title = target.Title, validHash, hash }, tx).ToArray();
        if (candidates.Length > 32) return new(false, "candidate_budget");
        var knownIdentities = new HashSet<(string?, string?, string?)>();
        var accepted = new List<(ReleaseForPoster Row, string Evidence)>();
        var reason = "no_candidate";
        ReleaseForPoster? lastDonor = null;
        IdentityReuseResult? historical = null;
        foreach (var candidate in candidates)
        {
            var donor = GetForReuse(candidate);
            lastDonor = donor;
            var sameHash = validHash && conn.ExecuteScalar<string?>("SELECT lower(info_hash) FROM releases WHERE id=@candidate", new { candidate }, tx) == hash;
            if (!LocalIdentityReuse.Corroborates(target, donor, sameHash)) { reason = "scope_or_evidence_insufficient"; continue; }
            if (!string.IsNullOrWhiteSpace(donor.ExtProviderId))
                knownIdentities.Add((donor.ExtProvider, donor.ExtProviderId, donor.TmdbMediaType));
            if (donor.TmdbId is > 0 && (donor.ExtProvider != "tmdb" || donor.ExtProviderId != donor.TmdbId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                return new(false, "contradictory_identities", candidate, donor.SourceId);
            // Require a provider-validated, release-owned snapshot agreeing with its entity.
            // Entity-only inherited metadata, manual matches and P4 descendants are not roots.
            if (donor.ExtProvider is not ("tmdb" or "tvmaze" or "igdb" or "rawg" or "jikan" or "musicbrainz" or "theaudiodb" or "googlebooks" or "openlibrary" or "comicvine"))
            { reason = "donor_unverified_or_manual"; continue; }
            var expectedScope = donor.ExtProvider switch
            {
                "tmdb" => donor.TmdbMediaType,
                "tvmaze" => "tv",
                "igdb" or "rawg" => "JeuWindows",
                "jikan" => "Anime",
                "musicbrainz" or "theaudiodb" => "Audio",
                "googlebooks" or "openlibrary" => "Book",
                "comicvine" => "Comic",
                _ => null
            };
            if (expectedScope != LocalIdentityReuse.Scope(target))
                return new(false, "contradictory_type", candidate, donor.SourceId, donor.ExtProvider, donor.ExtProviderId);
            var safeRoot = conn.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM releases r JOIN media_entities me ON me.id=r.entity_id
                WHERE r.id=@candidate AND me.manual_match_provider IS NULL AND me.retention_origin<>'manual'
                  AND COALESCE(r.title_manual_override,0)=0 AND COALESCE(r.poster_file,me.poster_file,'') NOT LIKE '%-manual%'
                  AND NOT EXISTS(SELECT 1 FROM local_identity_reuse WHERE release_id=r.id)
                  AND (r.ext_provider IS NULL OR me.ext_provider IS NULL OR r.ext_provider=me.ext_provider)
                  AND (r.ext_provider_id IS NULL OR me.ext_provider_id IS NULL OR r.ext_provider_id=me.ext_provider_id)
                  AND (r.tmdb_id IS NULL OR me.tmdb_id IS NULL OR r.tmdb_id=me.tmdb_id)
                  AND (r.tmdb_media_type IS NULL OR me.tmdb_media_type IS NULL OR r.tmdb_media_type=me.tmdb_media_type)
                  AND (r.tvdb_id IS NULL OR me.tvdb_id IS NULL OR r.tvdb_id=me.tvdb_id)
                """, new { candidate }, tx) == 1;
            if (!safeRoot) { reason = "donor_unverified_or_manual"; continue; }
            var claims = RssMatchingEvidence.Read(targetState.RssEvidenceJson);
            var donorClaims = RssMatchingEvidence.Read(conn.ExecuteScalar<string?>("SELECT raw_json FROM releases WHERE id=@candidate", new { candidate }, tx));
            if (donorClaims?.ConflictingClaims == true || donorClaims?.TmdbId is > 0 && donor.TmdbId is > 0 && donorClaims.TmdbId != donor.TmdbId ||
                donorClaims?.TvdbId is > 0 && donor.TvdbId is > 0 && donorClaims.TvdbId != donor.TvdbId || donorClaims?.ImdbId is not null &&
                conn.ExecuteScalar<string?>("SELECT imdb_id FROM media_entities WHERE id=@entity", new { entity = donor.EntityId }, tx) is { Length: > 0 } donorImdb && donorImdb != donorClaims.ImdbId)
                return new(false, "contradictory_donor_claims", candidate, donor.SourceId);
            if (claims?.ConflictingClaims == true || claims?.TmdbId is > 0 && donor.TmdbId is > 0 && claims.TmdbId != donor.TmdbId ||
                claims?.TvdbId is > 0 && donor.TvdbId is > 0 && claims.TvdbId != donor.TvdbId || claims?.ImdbId is not null &&
                conn.ExecuteScalar<string?>("SELECT imdb_id FROM media_entities WHERE id=@entity", new { entity = donor.EntityId }, tx) is { Length: > 0 } targetImdb && targetImdb != claims.ImdbId)
                return new(false, "contradictory_claims", candidate, donor.SourceId);
            var ownSnapshot = conn.ExecuteScalar<int>("SELECT COALESCE(ext_snapshot_version,0) FROM releases WHERE id=@candidate", new { candidate }, tx) == 1;
            var verified = donor.ExtSnapshotVersion == 1 &&
                (ownSnapshot || LocalIdentityReuse.MetadataCorroborates(donor, donor.ExtTitle, donor.ExtReleaseDate));
            var validated = validatedDonor?.Id == candidate && validatedDetails is not null &&
                LocalIdentityReuse.MetadataCorroborates(donor, validatedDetails.Title, validatedDetails.ReleaseDate) &&
                MatchingShadowState.From(validatedDonor) == MatchingShadowState.From(GetForPoster(candidate)!);
            if (!verified && !validated)
            {
                reason = "donor_unverified_or_manual";
                if (donor.ExtProvider == "tmdb" && donor.TmdbId is > 0 && donor.TmdbMediaType is "movie" or "tv")
                    historical ??= new(false, "historical_identity_requires_validation", candidate, donor.SourceId, donor.ExtProvider, donor.ExtProviderId);
                continue;
            }
            accepted.Add((donor, sameHash ? "infohash_and_scope" : "original_release_fingerprint"));
        }
        if (knownIdentities.Count > 1) return new(false, "contradictory_identities", lastDonor?.Id, lastDonor?.SourceId, lastDonor?.ExtProvider, lastDonor?.ExtProviderId);
        if (accepted.Count == 0) return historical ?? new(false, reason, lastDonor?.Id, lastDonor?.SourceId, lastDonor?.ExtProvider, lastDonor?.ExtProviderId);
        var chosen = accepted[0]; var root = chosen.Row;
        // Image provenance is independent from the metadata provider. Fanart and
        // legacy/unlabelled images cannot veto an otherwise verified media identity.
        var posterId = root.PosterProviderId;
        var imageBelongs = root.PosterProvider == root.ExtProvider &&
            (posterId == root.ExtProviderId || posterId == root.TmdbMediaType + ":" + root.ExtProviderId) ||
            root.ExtProvider == "tmdb" && root.PosterProvider == "fanart" &&
            (posterId == root.ExtProviderId || posterId == root.TmdbMediaType + ":" + root.ExtProviderId);

        imageBelongs &= !string.IsNullOrWhiteSpace(root.PosterFile) && Path.GetFileName(root.PosterFile) == root.PosterFile;
        // Relink just this release. Never overwrite the old shared entity or its siblings.
        conn.Execute("""
            UPDATE releases SET entity_id=@entity,poster_file=@file,poster_path=@path,
              poster_provider=@imageProvider,poster_provider_id=@imageId,poster_key=@key,
              poster_store_dir=@store,poster_hash=@hash,poster_lang=@lang,poster_size=@size,
              poster_last_error=NULL,poster_last_attempt_ts=unixepoch() WHERE id=@id;
            DELETE FROM poster_store_refs WHERE release_id=@id;
            INSERT OR IGNORE INTO poster_store_refs(store_dir,release_id,created_at_ts)
              SELECT @store,@id,unixepoch() WHERE @store IS NOT NULL;
            INSERT OR REPLACE INTO local_identity_reuse VALUES(@id,@donor,@source,@entity,@provider,@providerId,@evidence,unixepoch());
            """, new { id, entity = root.EntityId, file = imageBelongs ? root.PosterFile : null, path = imageBelongs ? root.PosterPath : null,
                provider = root.ExtProvider, providerId = root.ExtProviderId, key = imageBelongs ? root.PosterKey : null,
                store = imageBelongs ? root.PosterStoreDir : null, hash = imageBelongs ? root.PosterHash : null, lang = imageBelongs ? root.PosterLang : null, size = imageBelongs ? root.PosterSize : null,
                imageProvider = imageBelongs ? root.PosterProvider : null, imageId = imageBelongs ? root.PosterProviderId : null,
                donor = root.Id, source = root.SourceId, evidence = chosen.Evidence }, tx);
        if (validatedDonor?.Id == root.Id && validatedDetails is not null)
        {
            // Publish only this independently validated snapshot on the already-bound
            // entity. No ID is changed; old metadata is replaced rather than stamped trusted.
            var d = validatedDetails;
            UpdateExternalDetailsCore(conn, tx, id, root.ExtProvider!, root.ExtProviderId!, d.Title, d.Overview,
                d.Tagline, d.Genres, d.ReleaseDate, d.RuntimeMinutes, d.Rating, d.Votes, d.Directors, d.Writers, d.Cast,
                root.TmdbMediaType, validate: false);
        }
        AdvanceMatchingSnapshot(conn, tx, id);
        tx.Commit();
        return new(true, chosen.Evidence, root.Id, root.SourceId, root.ExtProvider, root.ExtProviderId) { ImageReusable = imageBelongs };

        ReleaseForPoster GetForReuse(long releaseId)
        {
            var row = GetForPoster(releaseId) ?? throw new StaleMatchException();
            if (row.ExtProvider is null && row.ExtProviderId is null && row.TmdbId is > 0 && row.TmdbMediaType is "movie" or "tv")
            { row.ExtProvider = "tmdb"; row.ExtProviderId = row.TmdbId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            return row;
        }
    }
}
