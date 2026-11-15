using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Services.Posters;

public sealed partial class PosterFetchService
{
    private async Task<PosterFetchResult> FetchGameV2Async(long releaseId, long? sourceId, CancellationToken ct)
    {
        PosterFetchResult Abstain(string reason, int status = 422) => new(false, status,
            new { error = "matching_v2_abstained", reason }, sourceId);
        try
        {
            var state = _releases.ReadMatchingShadowState(releaseId);
            if (state is null || state.Protected) return Abstain("protected_or_unavailable_identity", 409);
            _releases.ValidateMatching(releaseId);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var context = GameCandidateAdapter.Context(state.RawTitle ?? "");
            if (context.Queries.Count == 0 || context.Queries.Any(q => q.Title.Length > 200) ||
                System.Text.RegularExpressions.Regex.IsMatch(state.RawTitle ?? "", @"(?i)(https?://|www\.|@|(?:api[_-]?key|token|passkey|password)\s*[=:]|bearer\s+\S+)"))
                return Abstain("game_query_unsupported");
            if (!_igdb.MatchingEnabled && !_rawg.IsConfigured()) return Abstain("game_provider_not_configured", 503);
            var rows = new List<GameObservation>();
            async Task Collect(string provider)
            {
                foreach (var query in context.Queries.Select(q => q.Title).Distinct(StringComparer.Ordinal))
                {
                    var response = provider == "igdb"
                        ? await RunIgdbAsync(token => _igdb.ReadMatchingCandidatesAsync(query, token), timeout.Token).ConfigureAwait(false)
                        : await RunOthersAsync(token => _rawg.ReadMatchingCandidatesAsync(query, token), timeout.Token).ConfigureAwait(false);
                    rows.AddRange(GameCandidateAdapter.Project(provider, response));
                }
            }
            if (_igdb.MatchingEnabled)
            {
                try { await Collect("igdb").ConfigureAwait(false); }
                catch (InvalidDataException ex) when (ex.Message == "game_provider_unavailable" && _rawg.IsConfigured()) { }
                catch (HttpRequestException) when (_rawg.IsConfigured()) { }
            }
            var ranked = SearchCandidateRanker.Rank(context, rows.Select(r => r.Candidate).ToArray());
            // Prefer IGDB only after a complete, decisive ranking. RAWG cannot erase plausible IGDB rivals.
            if (ranked.Decision != CandidateDecision.Accept && _rawg.IsConfigured())
            {
                await Collect("rawg").ConfigureAwait(false);
                ranked = SearchCandidateRanker.Rank(context, rows.Select(r => r.Candidate).ToArray());
            }
            if (ranked.Decision != CandidateDecision.Accept || ranked.SelectedIdentity is not { } identity)
                return Abstain(ranked.Decision.ToString().ToLowerInvariant());
            if (_releases.ReadMatchingShadowState(releaseId) != state) throw new ReleaseRepository.StaleMatchException();
            _releases.ValidateMatching(releaseId);
            var selected = rows.First(r => r.Candidate.Identity == identity);
            var image = selected.Image;
            if (string.IsNullOrWhiteSpace(image)) return Abstain("selected_identity_poster_unavailable");
            var bytes = identity.Provider == "igdb"
                ? await RunIgdbAsync(token => _igdb.DownloadCoverAsync(image, token), timeout.Token).ConfigureAwait(false)
                : await RunOthersAsync(token => _rawg.DownloadCoverAsync(image, token), timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (bytes is not { Length: > 0 }) return Abstain("game_poster_unavailable", 503);
            var details = selected.Details;
            var extension = InferFileExtensionFromUrl(image, ".jpg");
            var file = $"{identity.Provider}-{identity.Id}-v2-{Guid.NewGuid():N}{extension}";
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
            catch (Exception) { _logger.LogWarning("Matching V2 derived game poster publication pending releaseId={ReleaseId}", releaseId); }
            return new(true, 200, new { ok = true, provider = identity.Provider, providerId = identity.Id, posterFile = file,
                posterUrl = $"/api/posters/release/{releaseId}", storePending = pending }, sourceId) { PublicationCommitted = true };
        }
        catch (ReleaseRepository.StaleMatchException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("game_", StringComparison.Ordinal))
        { return Abstain(ex.Message, ex.Message == "game_provider_unavailable" ? 503 : 422); }
        catch (Exception) { return Abstain("game_provider_or_publication_unavailable", 503); }
    }
}
