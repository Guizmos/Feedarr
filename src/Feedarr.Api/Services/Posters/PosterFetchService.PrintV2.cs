using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Services.Posters;

public sealed partial class PosterFetchService
{
    private async Task<PosterFetchResult> FetchPrintV2Async(long releaseId, long? sourceId, UnifiedCategory category, CancellationToken ct, bool skipIfExists = true)
    {
        PosterFetchResult Abstain(string reason, int status = 422) => new(false, status,
            new { error = "matching_v2_abstained", reason }, sourceId);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var state = _releases.ReadMatchingShadowState(releaseId);
            var known = _releases.GetForPoster(releaseId);
            if (state is null || known is null) return Abstain("print_release_unavailable", 404);
            _releases.ValidateMatching(releaseId);
            SearchIdentity identity;
            PrintObservation selected;
            if (state.Protected)
            {
                if (state.ManualProvider is not null || state.ManualTitle || state.LegacyManualPoster ||
                    state.ExternalProvider is not ("googlebooks" or "openlibrary" or "comicvine") || string.IsNullOrWhiteSpace(state.ExternalId))
                    return Abstain("print_protected_identity", 409);
                // Refresh artwork only for the bound identity; never search for a replacement.
                if (skipIfExists && !string.IsNullOrWhiteSpace(known.PosterFile) && File.Exists(Path.Combine(PostersDirAbs, known.PosterFile)))
                    return new(true, 200, new { ok = true, preserved = true, posterFile = known.PosterFile }, sourceId);
                identity = new(state.ExternalProvider, category == UnifiedCategory.Book ? SearchMediaType.Book : SearchMediaType.Comic, state.ExternalId);
                selected = new(new(identity, known.ExtTitle ?? ""), known.PosterPath,
                    new(known.ExtTitle, known.ExtOverview, known.ExtTagline, known.ExtGenres, known.ExtReleaseDate, known.ExtRuntimeMinutes,
                        known.ExtRating, known.ExtVotes, known.ExtDirectors, known.ExtWriters, known.ExtCast));
            }
            else
            {
                var raw = state.RawTitle ?? "";
                var (author, title) = category == UnifiedCategory.Book ? ParseBookQuery(raw) : (null, new Feedarr.Api.Services.Titles.TitleParser().Parse(raw, category).TitleClean ?? raw);
                var isbn = category == UnifiedCategory.Book ? TryExtractIsbn(raw) : null;
                if (isbn is not null) title = title.Replace(isbn, "", StringComparison.Ordinal).Trim();
                var context = PrintCandidateAdapter.Context(raw, category, title, author, isbn);
                if (title.Length > 200 || title.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(raw,
                    @"(?i)(https?://|www\.|@|(?:api[_-]?key|token|passkey|password)\s*[=:]|bearer\s+\S+)")) return Abstain("print_query_unsupported");
                var rows = new List<PrintObservation>();
                if (category == UnifiedCategory.Comic)
                {
                    var response = await RunOthersAsync(token => _comicVine.ReadMatchingAsync(title, null, null, token), timeout.Token).ConfigureAwait(false);
                    if (response is not null) rows.AddRange(PrintCandidateAdapter.ComicVine(response.Value));
                }
                else
                {
                    var response = await RunOthersAsync(token => _googleBooks.ReadMatchingAsync(title, isbn, author, token), timeout.Token).ConfigureAwait(false);
                    if (response is not null) rows.AddRange(PrintCandidateAdapter.Google(response.Value));
                }
                var ranked = SearchCandidateRanker.Rank(context, rows.Select(row => row.Candidate).ToArray());
                if (category == UnifiedCategory.Book && ranked.Decision != CandidateDecision.Accept)
                {
                    var response = await RunOthersAsync(token => _openLibrary.ReadMatchingAsync(title, isbn, author, token), timeout.Token).ConfigureAwait(false);
                    if (response is not null) rows.AddRange(PrintCandidateAdapter.OpenLibrary(response.Value, isbn is not null));
                    ranked = SearchCandidateRanker.Rank(context, rows.Select(row => row.Candidate).ToArray());
                }
                if (ranked.Decision != CandidateDecision.Accept || ranked.SelectedIdentity is not { } accepted)
                    return Abstain(ranked.Decision.ToString().ToLowerInvariant());
                if (rows.Count(row => row.Candidate.Identity == accepted) != 1) return Abstain("print_conflicting_provider_records");
                identity = accepted;
                selected = rows.First(r => r.Candidate.Identity == identity);
            }
            if (_releases.ReadMatchingShadowState(releaseId) != state) throw new ReleaseRepository.StaleMatchException();
            _releases.ValidateMatching(releaseId);
            var image = selected.Image;
            timeout.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(image)) return Abstain("print_selected_identity_poster_unavailable");
            var bytes = identity.Provider switch
            {
                "googlebooks" => await RunOthersAsync(token => _googleBooks.DownloadImageAsync(image, token), timeout.Token).ConfigureAwait(false),
                "openlibrary" => await RunOthersAsync(token => _openLibrary.DownloadImageAsync(image, token), timeout.Token).ConfigureAwait(false),
                "comicvine" => await RunOthersAsync(token => _comicVine.DownloadImageAsync(image, token), timeout.Token).ConfigureAwait(false),
                _ => null
            };
            timeout.Token.ThrowIfCancellationRequested();
            if (bytes is not { Length: > 0 }) return Abstain("print_poster_unavailable", 503);
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { _logger.LogWarning("Matching V2 derived print poster publication pending releaseId={ReleaseId}", releaseId); }
            return new(true, 200, new { ok = true, provider = identity.Provider, providerId = identity.Id, posterFile = file,
                posterUrl = $"/api/posters/release/{releaseId}", storePending = pending }, sourceId);
        }
        catch (ReleaseRepository.StaleMatchException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("print_", StringComparison.Ordinal))
        { return Abstain(ex.Message, ex.Message == "print_provider_unavailable" ? 503 : 422); }
        catch (Exception) { return Abstain("print_provider_or_publication_unavailable", 503); }
    }
}
