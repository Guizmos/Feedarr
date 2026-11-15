using System.Globalization;
using System.Text.RegularExpressions;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Posters;

public sealed partial class PosterFetchService
{
    private async Task<PosterFetchResult> PublishTvMazeV2Async(PosterFetchRoutingContext context, int showId, CancellationToken ct, bool refreshExisting = false, TmdbClient.DetailsResult? preservedDetails = null)
    {
        var raw = await RunTvMazeAsync(token => _tvmaze.GetMatchingEvidenceAsync($"shows/{showId}", token, refreshExisting || context.ForceRefresh), ct).ConfigureAwait(false);
        if (raw is not { } show || TelevisionCandidates.Number(show, "id") != showId)
            throw new InvalidDataException("tvmaze_publication_identity_unavailable");
        var name = TelevisionCandidates.Text(show, "name");
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("tvmaze_publication_title_unavailable");
        var image = show.TryGetProperty("image", out var images) ? TelevisionCandidates.Text(images, "original") ?? TelevisionCandidates.Text(images, "medium") : null;
        if (string.IsNullOrWhiteSpace(image))
            return new(false, 422, new { error = "matching_v2_abstained", reason = "selected_identity_poster_unavailable" }, context.SourceId);
        var summary = TelevisionCandidates.Text(show, "summary");
        var details = preservedDetails ?? new TmdbClient.DetailsResult(name, summary is null ? null : System.Net.WebUtility.HtmlDecode(Regex.Replace(summary, "<[^>]+>", "")),
            null, show.TryGetProperty("genres", out var genres) && genres.ValueKind == System.Text.Json.JsonValueKind.Array ?
                string.Join(", ", genres.EnumerateArray().Where(g => g.ValueKind == System.Text.Json.JsonValueKind.String).Select(g => g.GetString())) : null,
            TelevisionCandidates.Text(show, "premiered"), TelevisionCandidates.Number(show, "runtime"), null, null, null, null, null);
        if (!refreshExisting && await TryPublishSharedVideoPosterAsync(context, "tvmaze", showId.ToString(CultureInfo.InvariantCulture),
            null, "tv", details, ct).ConfigureAwait(false) is { } reused) return reused;
        var bytes = await RunTvMazeAsync(token => _tvmaze.DownloadImageAsync(image, token), ct).ConfigureAwait(false);
        if (bytes is not { Length: > 0 }) throw new InvalidDataException("tvmaze_poster_unavailable");
        var file = $"tvmaze-tv-{showId}-v2-{Guid.NewGuid():N}.jpg";
        var full = Path.Combine(PostersDirAbs, file);
        var committed = false;
        try
        {
            await WriteAtomicAsync(full, bytes, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _releases.PublishAcceptedVideo(context.ReleaseId, "tvmaze", showId.ToString(CultureInfo.InvariantCulture), null, "tv", image, file, details, refreshExisting: refreshExisting);
            committed = true;
        }
        finally
        {
            if (!committed)
                try { if (_releases.GetReferencedPosterFiles(new[] { file }).Count == 0) System.IO.File.Delete(full); }
                catch { /* An orphan is safer than removing a committed image. */ }
        }
        var pending = true;
        try
        {
            var materialized = await TryMaterializeStoreForReleaseAsync(context.ReleaseId, "tvmaze", showId.ToString(CultureInfo.InvariantCulture), file,
                bytes, full, false, null, null, ".jpg", null, ct).ConfigureAwait(false);
            pending = materialized.Status == StoreMaterializationStatus.Fail;
            _releases.ValidateMatching(context.ReleaseId);
            PosterAudit.UpdateAttemptSuccess(_releases, context.ReleaseId, "tvmaze", showId.ToString(CultureInfo.InvariantCulture), null, "original", ComputeSha256Hex(bytes));
        }
        catch (Feedarr.Api.Data.Repositories.ReleaseRepository.StaleMatchException) { throw; }
        catch (OperationCanceledException) { pending = true; }
        catch (Exception) { _logger.LogWarning("Matching V2 derived poster publication pending releaseId={ReleaseId}", context.ReleaseId); }
        return new(true, 200, new { ok = true, tvmazeId = showId, storePending = pending, posterFile = file, posterUrl = $"/api/posters/release/{context.ReleaseId}" }, context.SourceId) { PublicationCommitted = true };
    }
}
