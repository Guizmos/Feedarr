using System.Globalization;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Posters;

public sealed partial class PosterFetchService
{
    private async Task<PosterFetchResult> PublishMovieV2Async(PosterFetchRoutingContext context,
        TmdbClient.SearchResult selected, CancellationToken ct)
    {
        var id = context.ReleaseId;
        // No business mutation before all mandatory network work completes. Response IDs must agree,
        // not merely the requested URL; missing fields stay null, never inherited from another work.
        var details = await RunTmdbAsync(token => _tmdb.GetMoviePublicationDetailsAsync(selected.TmdbId, token), ct).ConfigureAwait(false);
        if (details is null) throw new InvalidDataException("tmdb_details_unavailable");
        var posterPath = await RunTmdbAsync(token => _tmdb.GetPreferredPosterPathAsync(selected.TmdbId, "movie", token, verifyIdentity: true), ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(posterPath)) posterPath = selected.PosterPath;
        if (string.IsNullOrWhiteSpace(posterPath))
            return new(false, 422, new { error = "matching_v2_abstained", reason = "selected_identity_poster_unavailable" }, context.SourceId);
        var bytes = await RunTmdbAsync(token => _tmdb.DownloadPosterW500Async(posterPath, token), ct).ConfigureAwait(false);
        if (bytes is not { Length: > 0 }) throw new InvalidDataException("tmdb_poster_unavailable");
        _releases.ValidateMatching(id);
        // Never replace a shared live file. A crash before commit can only leave an unreferenced file.
        var file = $"tmdb-movie-{selected.TmdbId}-v2-{Guid.NewGuid():N}.jpg";
        var full = Path.Combine(PostersDirAbs, file);
        var committed = false;
        try
        {
            await WriteAtomicAsync(full, bytes, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _releases.PublishAcceptedMovie(id, selected.TmdbId, posterPath, file, details);
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
        var storePending = true;
        try
        {
            var materialized = await TryMaterializeStoreForReleaseAsync(id, "tmdb", $"movie:{selected.TmdbId}", file,
                bytes, full, true, selected.TmdbId, posterPath, ".jpg", null, ct).ConfigureAwait(false);
            storePending = materialized.Status == StoreMaterializationStatus.Fail;
            _releases.ValidateMatching(id);
            PosterAudit.UpdateAttemptSuccess(_releases, id, "tmdb", selected.TmdbId.ToString(CultureInfo.InvariantCulture),
                null, "w500", ComputeSha256Hex(bytes));
        }
        catch (ReleaseRepository.StaleMatchException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            _logger.LogWarning("Matching V2 derived poster publication pending releaseId={ReleaseId}", id);
        }
        return new(true, 200, new { ok = true, tmdbId = selected.TmdbId, posterFile = file,
            posterUrl = $"/api/posters/release/{id}", storePending }, context.SourceId);
    }
}
