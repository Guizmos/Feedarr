using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Services.Posters;

public sealed partial class PosterFetchService
{
    private async Task<PosterFetchResult> FetchAudioV2Async(long releaseId, long? sourceId, CancellationToken ct, bool skipIfExists = true)
    {
        PosterFetchResult Abstain(string reason, int status = 422) => new(false, status,
            new { error = "matching_v2_abstained", reason }, sourceId);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var state = _releases.ReadMatchingShadowState(releaseId);
            var known = _releases.GetForPoster(releaseId);
            if (state is null || known is null) return Abstain("audio_release_unavailable", 404);
            _releases.ValidateMatching(releaseId);
            SearchIdentity identity;
            AudioObservation selected;
            if (state.Protected)
            {
                if (state.ManualProvider is not null || state.ManualTitle || state.LegacyManualPoster ||
                    state.ExternalProvider is not ("theaudiodb" or "musicbrainz") || string.IsNullOrWhiteSpace(state.ExternalId))
                    return Abstain("audio_protected_identity", 409);
                // Refresh artwork only for the bound identity; never search for a replacement.
                if (skipIfExists && !string.IsNullOrWhiteSpace(known.PosterFile) && File.Exists(Path.Combine(PostersDirAbs, known.PosterFile)))
                    return new(true, 200, new { ok = true, preserved = true, posterFile = known.PosterFile }, sourceId);
                identity = new(state.ExternalProvider, SearchMediaType.Audio, state.ExternalId);
                selected = new(new(identity, known.ExtTitle ?? ""), known.PosterPath,
                    new(known.ExtTitle, known.ExtOverview, known.ExtTagline, known.ExtGenres, known.ExtReleaseDate, known.ExtRuntimeMinutes,
                        known.ExtRating, known.ExtVotes, known.ExtDirectors, known.ExtWriters, known.ExtCast));
            }
            else
            {
                var (artist, title) = ParseAudioQuery(state.RawTitle ?? "");
                var context = AudioCandidateAdapter.Context(state.RawTitle ?? "", artist, title);
                if (string.IsNullOrWhiteSpace(artist)) return Abstain("audio_artist_unavailable");
                if (title.Length > 200 || artist.Length > 200 || System.Text.RegularExpressions.Regex.IsMatch(state.RawTitle ?? "",
                    @"(?i)(https?://|www\.|@|(?:api[_-]?key|token|passkey|password)\s*[=:]|bearer\s+\S+)")) return Abstain("audio_query_unsupported");
                var rows = new List<AudioObservation>();
                if (_theAudioDb.MatchingEnabled)
                    rows.AddRange(await RunOthersAsync(token => _theAudioDb.CollectMatchingAsync(context.Queries[0].Title, artist, token), timeout.Token).ConfigureAwait(false));
                if (_musicBrainz.MatchingEnabled)
                    rows.AddRange(await RunOthersAsync(token => _musicBrainz.CollectMatchingAsync(context.Queries[0].Title, artist, token), timeout.Token).ConfigureAwait(false));
                var ranked = SearchCandidateRanker.Rank(context, rows.Select(r => r.Candidate).ToArray());
                if (ranked.Decision != CandidateDecision.Accept || ranked.SelectedIdentity is not { } accepted)
                    return Abstain(ranked.Decision.ToString().ToLowerInvariant());
                if (rows.Count(row => row.Candidate.Identity == accepted) != 1) return Abstain("audio_conflicting_provider_records");
                identity = accepted;
                selected = rows.Single(r => r.Candidate.Identity == identity);
            }
            if (_releases.ReadMatchingShadowState(releaseId) != state) throw new ReleaseRepository.StaleMatchException();
            _releases.ValidateMatching(releaseId);
            var image = selected.Image;
            if (string.IsNullOrWhiteSpace(image) && identity.Provider == "musicbrainz")
                image = await RunOthersAsync(token => _musicBrainz.GetMatchingCoverAsync(identity.Id, token), timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(image)) return Abstain("audio_selected_identity_poster_unavailable");
            var bytes = identity.Provider == "theaudiodb"
                ? await RunOthersAsync(token => _theAudioDb.DownloadImageAsync(image, token), timeout.Token).ConfigureAwait(false)
                : await RunOthersAsync(token => _musicBrainz.DownloadImageAsync(image, token), timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (bytes is not { Length: > 0 }) return Abstain("audio_poster_unavailable", 503);
            var details = selected.Details;
            var extension = InferFileExtensionFromUrl(image, ".jpg");
            var file = $"{identity.Provider}-{SanitizeForFile(identity.Id)}-v2-{Guid.NewGuid():N}{extension}";
            var full = Path.Combine(PostersDirAbs, file); var committed = false;
            try
            {
                await WriteAtomicAsync(full, bytes, timeout.Token).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                _releases.PublishAcceptedVideo(releaseId, identity.Provider, identity.Id, null, null, image, file, details);
                committed = true;
            }
            finally
            {
                if (!committed)
                    try { if (_releases.GetReferencedPosterFiles(new[] { file }).Count == 0) System.IO.File.Delete(full); }
                    catch { /* Preserve a possible committed file on an uncertain commit outcome. */ }
            }
            var pending = true;
            try
            {
                var stored = await TryMaterializeStoreForReleaseAsync(releaseId, identity.Provider, identity.Id, file, bytes, full, false,
                    null, image, extension, null, timeout.Token).ConfigureAwait(false);
                pending = stored.Status == StoreMaterializationStatus.Fail;
                _releases.ValidateMatching(releaseId);
                PosterAudit.UpdateAttemptSuccess(_releases, releaseId, identity.Provider, identity.Id, null, "original", ComputeSha256Hex(bytes));
            }
            catch (ReleaseRepository.StaleMatchException) { throw; }
            catch (OperationCanceledException) { pending = true; }
            catch (Exception) { _logger.LogWarning("Matching V2 derived audio poster publication pending releaseId={ReleaseId}", releaseId); }
            return new(true, 200, new { ok = true, provider = identity.Provider, providerId = identity.Id, posterFile = file,
                posterUrl = $"/api/posters/release/{releaseId}", storePending = pending }, sourceId) { PublicationCommitted = true };
        }
        catch (ReleaseRepository.StaleMatchException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("audio_", StringComparison.Ordinal))
        { return Abstain(ex.Message, ex.Message == "audio_provider_unavailable" ? 503 : 422); }
        catch (Exception) { return Abstain("audio_provider_or_publication_unavailable", 503); }
    }
}
