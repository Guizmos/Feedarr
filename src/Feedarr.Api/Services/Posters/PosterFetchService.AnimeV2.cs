using System.Globalization;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Posters;

public sealed partial class PosterFetchService
{
    private async Task<PosterFetchResult> FetchAnimeV2Async(long releaseId, long? sourceId, CancellationToken ct)
    {
        PosterFetchResult Abstain(string reason, int status = 422) => new(false, status,
            new { error = "matching_v2_abstained", reason }, sourceId);
        try
        {
            var state = _releases.ReadMatchingShadowState(releaseId);
            if (state is null || state.Protected) return Abstain("protected_or_unavailable_identity", 409);
            _releases.ValidateMatching(releaseId);
            if (!_jikan.MatchingEnabled) return Abstain("jikan_not_configured", 503);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var rows = await JikanCandidateAdapter.CollectAsync(state.RawTitle ?? "",
                (path, token) => RunOthersAsync(inner => _jikan.ReadMatchingAsync(path, inner), token), timeout.Token).ConfigureAwait(false);
            var ranked = SearchCandidateRanker.Rank(JikanCandidateAdapter.Context(state.RawTitle ?? ""), rows.Select(r => r.Candidate).ToArray());
            if (ranked.Decision != CandidateDecision.Accept || ranked.SelectedIdentity is not { } identity)
                return Abstain(ranked.Decision.ToString().ToLowerInvariant());
            if (_releases.ReadMatchingShadowState(releaseId) != state) throw new ReleaseRepository.StaleMatchException();
            _releases.ValidateMatching(releaseId);
            var selected = rows.Single(r => r.Candidate.Identity == identity);
            var raw = selected.Details;
            string? image = null;
            if (raw.TryGetProperty("images", out var images) && images.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var format in new[] { "jpg", "webp" })
                    if (image is null && images.TryGetProperty(format, out var paths)) image = JikanCandidateAdapter.Text(paths, "large_image_url") ?? JikanCandidateAdapter.Text(paths, "image_url");
            if (string.IsNullOrWhiteSpace(image)) return Abstain("selected_identity_poster_unavailable");
            var bytes = await RunOthersAsync(token => _jikan.DownloadImageAsync(image, token), timeout.Token).ConfigureAwait(false);
            if (bytes is not { Length: > 0 }) return Abstain("jikan_poster_unavailable", 503);
            var englishTitle = JikanCandidateAdapter.ProviderTitle(raw, "English", "title_english");
            var title = string.IsNullOrWhiteSpace(englishTitle) ? JikanCandidateAdapter.ProviderTitle(raw, "Default", "title") : englishTitle;
            var genres = raw.TryGetProperty("genres", out var genreList) && genreList.ValueKind == System.Text.Json.JsonValueKind.Array
                ? string.Join(", ", genreList.EnumerateArray().Select(g => JikanCandidateAdapter.Text(g, "name")).Where(g => !string.IsNullOrWhiteSpace(g))) : null;
            var date = raw.TryGetProperty("aired", out var aired) ? JikanCandidateAdapter.Text(aired, "from") : null;
            var details = new TmdbClient.DetailsResult(title, JikanCandidateAdapter.Text(raw, "synopsis"), null, genres,
                date, null, raw.TryGetProperty("score", out var score) && score.ValueKind == System.Text.Json.JsonValueKind.Number && score.TryGetDouble(out var rating) && rating > 0 ? rating : null,
                null, null, null, null);
            var extension = InferFileExtensionFromUrl(image, ".jpg");
            var file = $"jikan-{identity.Id}-v2-{Guid.NewGuid():N}{extension}";
            var full = Path.Combine(PostersDirAbs, file); var committed = false;
            try
            {
                await WriteAtomicAsync(full, bytes, timeout.Token).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                _releases.PublishAcceptedVideo(releaseId, "jikan", identity.Id, null, null, image, file, details, selected.Candidate.Anime);
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
                var stored = await TryMaterializeStoreForReleaseAsync(releaseId, "jikan", identity.Id, file, bytes, full, false,
                    null, image, extension, null, timeout.Token).ConfigureAwait(false);
                pending = stored.Status == StoreMaterializationStatus.Fail;
                _releases.ValidateMatching(releaseId);
                PosterAudit.UpdateAttemptSuccess(_releases, releaseId, "jikan", identity.Id, null, "original", ComputeSha256Hex(bytes));
            }
            catch (ReleaseRepository.StaleMatchException) { throw; }
            catch (OperationCanceledException) { pending = true; }
            catch (Exception) { _logger.LogWarning("Matching V2 derived anime poster publication pending releaseId={ReleaseId}", releaseId); }
            return new(true, 200, new { ok = true, provider = "jikan", providerId = identity.Id, posterFile = file,
                posterUrl = $"/api/posters/release/{releaseId}", storePending = pending }, sourceId) { PublicationCommitted = true };
        }
        catch (ReleaseRepository.StaleMatchException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("jikan_", StringComparison.Ordinal))
        {
            // Deterministic limits cannot improve through an immediate provider retry.
            var bounded = ex.Message is "jikan_page_budget" or "jikan_candidate_budget" or "jikan_evidence_budget" or "jikan_http_budget" or "jikan_query_unsupported" or "jikan_shared_entity_scope_unverified";
            return Abstain(ex.Message, bounded ? 422 : 503);
        }
        catch (Exception) { return Abstain("jikan_provider_or_publication_unavailable", 503); }
    }
}
