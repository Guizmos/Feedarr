using System.Globalization;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Posters;

public sealed partial class PosterFetchService
{
    private async Task<PosterFetchResult> RefreshEstablishedMatchAsync(ReleaseForPoster known, CancellationToken ct, bool skipIfExists)
    {
        var id = known.Id;
        _releases.ValidateMatching(id);
        if (skipIfExists && !string.IsNullOrWhiteSpace(known.PosterFile) && File.Exists(Path.Combine(PostersDirAbs, known.PosterFile)))
            return new(true, 200, new { ok = true, preserved = true, posterFile = known.PosterFile }, known.SourceId);
        var provider = known.ManualMatchProvider ?? known.ExtProvider ?? (known.TmdbId is > 0 ? "tmdb" : known.PosterProvider);
        var providerId = known.ManualMatchId ?? known.ExtProviderId ?? (known.TmdbId is > 0 ? known.TmdbId.Value.ToString(CultureInfo.InvariantCulture) : known.PosterProviderId);
        PosterFetchResult Unavailable(string reason, int status = 422) => known.ManualMatchProvider is not null && skipIfExists
            ? new(true, 200, new { ok = true, manual = true, preserved = true, refreshUnavailable = reason }, known.SourceId)
            : new(false, status, new { error = "identity_refresh_unavailable", reason, preserved = true }, known.SourceId);
        if (provider is null || string.IsNullOrWhiteSpace(providerId)) return Unavailable("bound_identity_unavailable", 409);
        if (known.ManualMatchProvider is null &&
            (known.TmdbId is > 0 && provider != "tmdb" || known.TvdbId is > 0 && provider is not ("tmdb" or "tvmaze")))
            return Unavailable("conflicting_historical_identity", 409);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            UnifiedCategoryMappings.TryParse(known.UnifiedCategory, out var category);
            var context = new PosterFetchRoutingContext(id, category, IsTvmazeEnabled(), true, known.SourceId);
            if (provider == "tmdb")
            {
                var type = TmdbIdentity.NormalizeType(known.ManualMatchType ?? known.TmdbMediaType);
                if (type is null || !int.TryParse(providerId, out var tmdbId) || tmdbId <= 0) return Unavailable("historical_identity_type_required", 409);
                return await PublishVideoV2Async(context,
                    new TmdbClient.SearchResult(tmdbId, known.ExtTitle ?? known.Title ?? "", null, null, type, known.Year, null), deadline.Token, refreshExisting: true).ConfigureAwait(false);
            }
            if (provider == "tvmaze" && int.TryParse(providerId, out var showId) && showId > 0)
                return await PublishTvMazeV2Async(context, showId, deadline.Token, refreshExisting: true).ConfigureAwait(false);
            // A persisted URL belongs to the already-bound identity. In particular, do
            // not infer track/album or issue/volume from an ambiguous historical number.
            var image = known.PosterPath;
            if (string.IsNullOrWhiteSpace(image)) return Unavailable("bound_poster_url_unavailable");
            var bytes = provider switch
            {
                "igdb" => await RunIgdbAsync(token => _igdb.DownloadCoverAsync(image, token), deadline.Token).ConfigureAwait(false),
                "rawg" => await RunOthersAsync(token => _rawg.DownloadCoverAsync(image, token), deadline.Token).ConfigureAwait(false),
                "jikan" => await RunOthersAsync(token => _jikan.DownloadImageAsync(image, token), deadline.Token).ConfigureAwait(false),
                "theaudiodb" => await RunOthersAsync(token => _theAudioDb.DownloadImageAsync(image, token), deadline.Token).ConfigureAwait(false),
                "musicbrainz" => await RunOthersAsync(token => _musicBrainz.DownloadImageAsync(image, token), deadline.Token).ConfigureAwait(false),
                "comicvine" => await RunOthersAsync(token => _comicVine.DownloadImageAsync(image, token), deadline.Token).ConfigureAwait(false),
                "googlebooks" => await RunOthersAsync(token => _googleBooks.DownloadImageAsync(image, token), deadline.Token).ConfigureAwait(false),
                "openlibrary" => await RunOthersAsync(token => _openLibrary.DownloadImageAsync(image, token), deadline.Token).ConfigureAwait(false),
                _ => null
            };
            deadline.Token.ThrowIfCancellationRequested();
            if (bytes is not { Length: > 0 }) return Unavailable("bound_poster_download_failed", 503);
            var file = $"{provider}-{SanitizeForFile(providerId)}-refresh-{Guid.NewGuid():N}{InferFileExtensionFromUrl(image, ".jpg")}";
            var full = Path.Combine(PostersDirAbs, file); var committed = false;
            try
            {
                await WriteAtomicAsync(full, bytes, deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                var details = new TmdbClient.DetailsResult(known.ExtTitle, known.ExtOverview, known.ExtTagline, known.ExtGenres, known.ExtReleaseDate,
                    known.ExtRuntimeMinutes, known.ExtRating, known.ExtVotes, known.ExtDirectors, known.ExtWriters, known.ExtCast);
                _releases.PublishAcceptedVideo(id, provider, providerId, null, null, image, file, details, refreshExisting: true);
                committed = true;
            }
            finally
            {
                if (!committed)
                    try { if (_releases.GetReferencedPosterFiles(new[] { file }).Count == 0) File.Delete(full); }
                    catch { /* Keep a possibly committed file. */ }
            }
            var pending = true;
            try
            {
                var stored = await TryMaterializeStoreForReleaseAsync(id, provider, providerId, file, bytes, full, true,
                    null, image, InferFileExtensionFromUrl(image, ".jpg"), null, deadline.Token).ConfigureAwait(false);
                pending = stored.Status == StoreMaterializationStatus.Fail;
            }
            catch (ReleaseRepository.StaleMatchException) { throw; }
            catch (OperationCanceledException) { pending = true; }
            catch (Exception) { _logger.LogWarning("Derived refreshed poster pending releaseId={ReleaseId}", id); }
            return new(true, 200, new { ok = true, refreshed = true, posterFile = file, storePending = pending }, known.SourceId) { PublicationCommitted = true };
        }
        catch (ReleaseRepository.StaleMatchException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Unavailable("provider_or_publication_unavailable", 503); }
    }
}
