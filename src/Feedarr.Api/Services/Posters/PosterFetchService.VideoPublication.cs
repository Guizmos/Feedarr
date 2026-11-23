using System.Globalization;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Posters;

public sealed partial class PosterFetchService
{
    private async Task<PosterFetchResult> PublishVideoV2Async(PosterFetchRoutingContext context,
        TmdbClient.SearchResult selected, CancellationToken ct, bool refreshExisting = false, TmdbClient.DetailsResult? preservedDetails = null, Action<string, string?>? diagnosticStage = null, Action<Exception>? diagnosticFailure = null)
    {
        var id = context.ReleaseId;
        var type = selected.MediaType == "tv" ? "tv" : "movie";
        // No business mutation before all mandatory network work completes. Response IDs must agree,
        // not merely the requested URL; missing fields stay null, never inherited from another work.
        var details = preservedDetails ?? await RunTmdbAsync(token => type == "tv" ? _tmdb.GetTvPublicationDetailsAsync(selected.TmdbId, token, refreshExisting || context.ForceRefresh) : _tmdb.GetMoviePublicationDetailsAsync(selected.TmdbId, token, refreshExisting || context.ForceRefresh), ct).ConfigureAwait(false);
        if (details is null) throw new InvalidDataException("tmdb_details_unavailable");
        if (!refreshExisting && await TryPublishSharedVideoPosterAsync(context, "tmdb", selected.TmdbId.ToString(CultureInfo.InvariantCulture),
            selected.TmdbId, type, details, ct, diagnosticStage).ConfigureAwait(false) is { } reused) return reused;
        var posterPath = await RunTmdbAsync(token => _tmdb.GetPreferredPosterPathAsync(selected.TmdbId, type == "tv" ? "series" : "movie", token, verifyIdentity: true), ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(posterPath)) posterPath = details.PosterPath;
        if (string.IsNullOrWhiteSpace(posterPath)) posterPath = selected.PosterPath;
        var posterProvider = "tmdb";
        byte[]? bytes = null;
        if (!string.IsNullOrWhiteSpace(posterPath))
        {
            try { bytes = await RunTmdbAsync(token => _tmdb.DownloadPosterW500Async(posterPath, token), ct).ConfigureAwait(false); }
            catch (HttpRequestException) when (type == "tv" || context.UnifiedCategory is (UnifiedCategory.Animation or UnifiedCategory.Spectacle))
            {
                // Only the image download failed. Metadata/identity errors and cancellation
                // still propagate; the fallback below cannot choose a different movie.
            }
        }
        // Preserve the historical image fallback for the migrated categories only.
        // Fanart enriches the one accepted movie ID; it never selects another work.
        if (bytes is not { Length: > 0 } && (type == "tv" || context.UnifiedCategory is (UnifiedCategory.Animation or UnifiedCategory.Spectacle)))
        {
            string? fanartPath;
            if (type == "tv")
            {
                var raw = await RunTmdbAsync(token => _tmdb.GetTelevisionEvidenceAsync($"tv/{selected.TmdbId}?append_to_response=external_ids", token), ct).ConfigureAwait(false);
                if (raw is not { } show || Feedarr.Api.Services.Matching.TelevisionCandidates.Number(show, "id") != selected.TmdbId)
                    throw new InvalidDataException("tmdb_tv_external_identity_unavailable");
                var tvdbId = show.TryGetProperty("external_ids", out var external) ? Feedarr.Api.Services.Matching.TelevisionCandidates.Number(external, "tvdb_id") : null;
                fanartPath = tvdbId is > 0 ? await RunFanartAsync(token => _fanart.GetTvPosterUrlAsync(tvdbId.Value, token, selected.OriginalLanguage), ct).ConfigureAwait(false) : null;
            }
            else fanartPath = await RunFanartAsync(token => _fanart.GetMoviePosterUrlAsync(selected.TmdbId, token, selected.OriginalLanguage), ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(fanartPath))
            {
                bytes = await RunFanartAsync(token => _fanart.DownloadAsync(fanartPath, token), ct).ConfigureAwait(false);
                if (bytes is { Length: > 0 }) { posterPath = fanartPath; posterProvider = "fanart"; }
            }
        }
        if (bytes is not { Length: > 0 } || string.IsNullOrWhiteSpace(posterPath))
        {
            if (string.IsNullOrWhiteSpace(posterPath))
            {
                diagnosticStage?.Invoke("preparation", "tmdb");
                PosterAudit.UpdateAttemptFailure(_releases, id, null, null, null, null,
                    "v2:preparation:business_abstention:selected_identity_poster_unavailable");
                return new(false, 422, new { error = "matching_v2_abstained", reason = "selected_identity_poster_unavailable" }, context.SourceId);
            }
            throw new InvalidDataException("tmdb_poster_unavailable");
        }
        _releases.ValidateMatching(id);
        // Never replace a shared live file. A crash before commit can only leave an unreferenced file.
        var file = $"{posterProvider}-{type}-{selected.TmdbId}-v2-{Guid.NewGuid():N}.jpg";
        var full = Path.Combine(PostersDirAbs, file);
        var committed = false;
        try
        {
            diagnosticStage?.Invoke("publication", null);
            await WriteAtomicAsync(full, bytes, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _releases.PublishAcceptedVideo(id, "tmdb", selected.TmdbId.ToString(CultureInfo.InvariantCulture), selected.TmdbId, type, posterPath, file, details, refreshExisting: refreshExisting);
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                // Never delete a possibly committed image after an uncertain commit outcome.
                try { if (_releases.GetReferencedPosterFiles(new[] { file }).Count == 0) System.IO.File.Delete(full); }
                catch { /* Keep an orphan rather than risk removing a referenced image. */ }
            }
        }
        // Store/thumbnails/cache are derived optimizations. Their failure must leave the fully usable
        // legacy image + committed metadata intact, and must never trigger another identity choice.
        diagnosticStage?.Invoke("materialization", null);
        var storePending = true;
        try
        {
            var materialized = await TryMaterializeStoreForReleaseAsync(id, posterProvider, $"{type}:{selected.TmdbId}", file,
                bytes, full, true, selected.TmdbId, posterPath, ".jpg", null, ct).ConfigureAwait(false);
            storePending = materialized.Status == StoreMaterializationStatus.Fail;
            _releases.ValidateMatching(id);
            PosterAudit.UpdateAttemptSuccess(_releases, id, posterProvider,
                posterProvider == "tmdb" ? selected.TmdbId.ToString(CultureInfo.InvariantCulture) : $"{type}:{selected.TmdbId}",
                null, posterProvider == "tmdb" ? "w500" : null, ComputeSha256Hex(bytes));
        }
        catch (ReleaseRepository.StaleMatchException) { throw; }
        catch (OperationCanceledException error) { storePending = true; diagnosticFailure?.Invoke(error); }
        catch (Exception error)
        {
            diagnosticFailure?.Invoke(error);
            _logger.LogWarning("Matching V2 materialization pending releaseId={ReleaseId} exceptionType={ExceptionType}", id, error.GetType().Name);
        }
        if (storePending) diagnosticFailure?.Invoke(new IOException());
        return new(true, 200, new { ok = true, tmdbId = selected.TmdbId, posterFile = file,
            posterUrl = $"/api/posters/release/{id}", storePending }, context.SourceId) { PublicationCommitted = true };
    }
}
