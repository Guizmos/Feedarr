using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Services.Posters;

public sealed partial class PosterFetchService
{
    private async Task<PosterFetchResult> FetchGameV2Async(long releaseId, long? sourceId, CancellationToken ct)
    {
        var stage = "routing";
        string? provider = null;
        int? candidateCount = null;
        int? providerResultCount = null;
        string? igdbDiagnostic = null;
        object? igdbCollectionFailure = null;
        string? rawgFailureReason = null;
        int? rawgHttpStatus = null;
        var rawgRequestSuppressed = false;
        void CaptureRawgFailure(Exception error)
        {
            rawgFailureReason = error.Data["RawgFailureReason"] as string;
            rawgHttpStatus = error.Data["RawgHttpStatus"] as int?;
            rawgRequestSuppressed = error.Data["RawgRequestSuppressed"] is true;
        }
        var queriedProviders = new HashSet<string>(StringComparer.Ordinal);
        void Record(string reason, string errorCategory, string exceptionType = "none", bool persist = true)
        {
            MatchingAttemptDiagnostics.Current?.SetOutcome(stage, reason);
            if (stage is "collection" or "projection") MatchingAttemptDiagnostics.Current?.Collection(provider ?? "game_search", reason);
            // Only controlled codes/counters: never titles, response bodies, URLs or exception messages.
            var providers = string.Join("+", queriedProviders.Order());
            _logger.LogWarning("Matching V2 diagnostic releaseId={ReleaseId} category={Category} stage={Stage} provider={Provider} providersQueried={ProvidersQueried} candidateCount={CandidateCount} providerResultCount={ProviderResultCount} errorCategory={ErrorCategory} exceptionType={ExceptionType} attemptedAtTs={AttemptedAtTs} decision={Decision} igdbDiagnostic={IgdbDiagnostic} rawgFailureReason={RawgFailureReason} rawgHttpStatus={RawgHttpStatus} rawgRequestSuppressed={RawgRequestSuppressed}",
                releaseId, "JeuWindows", stage, provider, providers, candidateCount, providerResultCount, errorCategory, exceptionType, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), reason, igdbDiagnostic, rawgFailureReason, rawgHttpStatus, rawgRequestSuppressed);
            if (!persist) return;
            try
            {
                PosterAudit.UpdateAttemptFailure(_releases, releaseId, null, null, null, null,
                    $"v2:{stage}:{errorCategory}:{reason};category=JeuWindows;provider={provider ?? "none"};queried={providers};candidates={candidateCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"};providerResults={providerResultCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}" +
                    (igdbDiagnostic is null ? "" : ";igdb=" + igdbDiagnostic) +
                    (rawgFailureReason is null ? "" : ";rawg=" + System.Text.Json.JsonSerializer.Serialize(new {
                        Stage = rawgFailureReason == "rawg_configuration_unavailable" ? "configuration" : stage,
                        Reason = rawgFailureReason, HttpStatus = rawgHttpStatus, RequestSuppressed = rawgRequestSuppressed })));
            }
            catch (Exception error)
            {
                // Diagnostic failure must not change the matching result, or overwrite a
                // concurrent manual association rejected by ValidateMatching.
                _logger.LogWarning("Matching V2 diagnostic persistence skipped releaseId={ReleaseId} exceptionType={ExceptionType}", releaseId, error.GetType().Name);
            }
        }
        PosterFetchResult Abstain(string reason, int status = 422, string? diagnosticReason = null,
            string errorCategory = "business_abstention", string exceptionType = "none", bool persist = true)
        {
            Record(diagnosticReason ?? reason, errorCategory, exceptionType, persist);
            return new(false, status, new { error = "matching_v2_abstained", reason }, sourceId) { Retryable = rawgHttpStatus is not (400 or 401 or 403) && rawgFailureReason != "rawg_quota_exhausted" };
        }
        try
        {
            var state = _releases.ReadMatchingShadowState(releaseId);
            if (state is null || state.Protected) return Abstain("protected_or_unavailable_identity", 409, persist: false);
            _releases.ValidateMatching(releaseId);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            stage = "query";
            var context = GameCandidateAdapter.Context(state.RawTitle ?? "");
            var queryScope = GameSearchScope.Read(state.RawTitle ?? "");
            _logger.LogInformation("Matching V2 game query scope releaseId={ReleaseId} edition={GameEdition} contentScope={ContentScope} platformOrigin={PlatformOrigin} queryCount={QueryCount}",
                releaseId, context.GameEdition, queryScope.Content, queryScope.PlatformOrigin, context.Queries.Count);
            if (context.Queries.Count == 0 || context.Queries.Any(q => q.Title.Length > 200) ||
                System.Text.RegularExpressions.Regex.IsMatch(state.RawTitle ?? "", @"(?i)(https?://|www\.|@|(?:api[_-]?key|token|passkey|password)\s*[=:]|bearer\s+\S+)"))
                return Abstain("game_query_unsupported");
            if (!_igdb.MatchingEnabled && !_rawg.IsConfigured())
            {
                rawgFailureReason = "rawg_configuration_unavailable";
                return Abstain("game_provider_not_configured", 503, errorCategory: "provider_error");
            }
            var rows = new List<GameObservation>();
            async Task Collect(string source)
            {
                provider = source;
                MatchingAttemptDiagnostics.Current?.SetStrategy(string.Join("_then_", queriedProviders.Append(source).Distinct()));
                foreach (var query in context.Queries.Select(q => q.Title).Distinct(StringComparer.Ordinal))
                {
                    stage = "collection";
                    providerResultCount = null;
                    queriedProviders.Add(source);
                    var response = source == "igdb"
                        ? await RunIgdbAsync(token => _igdb.ReadMatchingCandidatesAsync(query, token), timeout.Token).ConfigureAwait(false)
                        : await RunOthersAsync(token => _rawg.ReadMatchingCandidatesAsync(query, token, status => rawgHttpStatus = status), timeout.Token).ConfigureAwait(false);
                    stage = "projection";
                    if (response.ValueKind == System.Text.Json.JsonValueKind.Array) providerResultCount = response.GetArrayLength();
                    else if (response.ValueKind == System.Text.Json.JsonValueKind.Object && response.TryGetProperty("count", out var count) &&
                        count.ValueKind == System.Text.Json.JsonValueKind.Number && count.TryGetInt32(out var total)) providerResultCount = total;
                    try { rows.AddRange(GameCandidateAdapter.Project(source, response, completeIgdbCollection: source == "igdb"));
                        MatchingAttemptDiagnostics.Current?.Collection(source, "complete"); }
                    catch (Exception error) when (source == "rawg")
                    {
                        // Partial but valid pagination remains a coverage abstention.
                        var partial = error is InvalidDataException { Message: "game_search_coverage_unavailable" } &&
                            response.ValueKind == System.Text.Json.JsonValueKind.Object &&
                            response.TryGetProperty("results", out var results) && results.ValueKind == System.Text.Json.JsonValueKind.Array &&
                            response.TryGetProperty("count", out var totalCount) && totalCount.ValueKind == System.Text.Json.JsonValueKind.Number &&
                            totalCount.TryGetInt32(out var totalResults) && totalResults >= results.GetArrayLength() &&
                            response.TryGetProperty("next", out var next) && next.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.String;
                        Feedarr.Api.Services.Rawg.RawgClient.AnnotateMatchingFailure(error,
                            partial ? "rawg_coverage_unavailable" : "rawg_invalid_response", rawgHttpStatus);
                        throw;
                    }
                    candidateCount = rows.Count;
                }
            }
            if (_igdb.MatchingEnabled)
            {
                try { await Collect("igdb").ConfigureAwait(false); }
                catch (InvalidDataException ex) when (ex.Message == "game_provider_unavailable" && _rawg.IsConfigured())
                { igdbCollectionFailure = new { Stage = "collection", Reason = "game_provider_unavailable", ExceptionType = ex.GetType().Name }; Record("game_provider_unavailable", "provider_error", ex.GetType().Name, persist: false); }
                catch (HttpRequestException error) when (_rawg.IsConfigured())
                { igdbCollectionFailure = new { Stage = "collection", Reason = "game_provider_unavailable", ExceptionType = error.GetType().Name }; Record("game_provider_unavailable", "provider_error", error.GetType().Name, persist: false); }
            }
            stage = "ranking";
            candidateCount = rows.Count;
            provider = string.Join("+", queriedProviders.Order());
            var ranked = SearchCandidateRanker.Rank(context, rows.Select(r => r.Candidate).ToArray());
            // Prefer IGDB only after a complete, decisive ranking. RAWG cannot erase plausible IGDB rivals.
            if (ranked.Decision != CandidateDecision.Accept)
            {
                // Snapshot the existing ranking before RAWG can fail or add rivals.
                // Only ranker codes, numeric identities and scores; no provider text.
                if (queriedProviders.Contains("igdb"))
                {
                    MatchingAttemptDiagnostics.LogRanking(_logger, releaseId, ranked);
                    igdbDiagnostic = System.Text.Json.JsonSerializer.Serialize(new {
                        Stage = "ranking", Decision = ranked.Decision.ToString().ToLowerInvariant(),
                        ranked.Reasons, CandidateCount = rows.Count, CollectionFailure = igdbCollectionFailure,
                        CandidateLimit = 6, CandidatesTruncated = ranked.Candidates.Count > 6,
                        Candidates = ranked.Candidates.Take(6).Select(candidate => new {
                            candidate.Identity, candidate.QueryIndex, candidate.Confidence, candidate.EvidenceScore,
                            Decision = candidate.Decision.ToString().ToLowerInvariant(), candidate.Contradictions
                        })
                    });
                }
                if (_rawg.IsConfigured())
                {
                    await Collect("rawg").ConfigureAwait(false);
                    stage = "ranking";
                    provider = string.Join("+", queriedProviders.Order());
                    ranked = SearchCandidateRanker.Rank(context, rows.Select(r => r.Candidate).ToArray());
                }
                else rawgFailureReason = "rawg_configuration_unavailable";
            }
            MatchingAttemptDiagnostics.LogRanking(_logger, releaseId, ranked);
            if (ranked.Decision != CandidateDecision.Accept || ranked.SelectedIdentity is not { } identity)
                return Abstain(ranked.Decision.ToString().ToLowerInvariant(),
                    diagnosticReason: rows.Count == 0 ? "no_candidates" : ranked.Decision == CandidateDecision.Reject ? "candidates_rejected" : "ambiguous",
                    errorCategory: rows.Count == 0 ? "no_candidates" : ranked.Decision == CandidateDecision.Reject ? "contradiction" : "ambiguity");
            if (_releases.ReadMatchingShadowState(releaseId) != state) throw new ReleaseRepository.StaleMatchException();
            _releases.ValidateMatching(releaseId);
            var selected = rows.First(r => r.Candidate.Identity == identity);
            stage = "image_selection";
            provider = identity.Provider;
            var image = selected.Image;
            if (string.IsNullOrWhiteSpace(image)) return Abstain("selected_identity_poster_unavailable", errorCategory: "poster_unavailable");
            stage = "download";
            var bytes = identity.Provider == "igdb"
                ? await RunIgdbAsync(token => _igdb.DownloadCoverAsync(image, token), timeout.Token).ConfigureAwait(false)
                : await RunOthersAsync(token => _rawg.DownloadCoverAsync(image, token), timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (bytes is not { Length: > 0 }) return Abstain("game_poster_unavailable", 503, errorCategory: "poster_unavailable");
            var details = selected.Details;
            var extension = InferFileExtensionFromUrl(image, ".jpg");
            var file = $"{identity.Provider}-{identity.Id}-v2-{Guid.NewGuid():N}{extension}";
            var full = Path.Combine(PostersDirAbs, file); var committed = false;
            try
            {
                stage = "publication";
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
            stage = "materialization";
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
        {
            CaptureRawgFailure(ex);
            if (ex.Data["IgdbCoverageReason"] is string coverageReason)
            {
                var detail = new { Reason = coverageReason, Offset = ex.Data["IgdbOffset"], Limit = ex.Data["IgdbLimit"],
                    UniqueCount = ex.Data["IgdbUniqueCount"], Requests = ex.Data["IgdbRequests"] };
                igdbDiagnostic = System.Text.Json.JsonSerializer.Serialize(detail);
                MatchingAttemptDiagnostics.Current?.Collection("igdb_single_window", coverageReason, ex.Data["IgdbRequests"] as int?);
            }
            if (ex.Data["ProviderResultCount"] is int observedCount) providerResultCount = observedCount;
            var code = ex.Message is "game_provider_unavailable" or "game_search_coverage_unavailable" or
                "game_search_identity_invalid" or "game_shared_entity_scope_unverified" ? ex.Message : "game_invariant_unavailable";
            return Abstain(ex.Message, ex.Message == "game_provider_unavailable" ? 503 : 422, diagnosticReason: code,
                errorCategory: code == "game_search_coverage_unavailable" ? "coverage_insufficient" :
                    stage == "publication" ? "publication_error" : "provider_error", exceptionType: ex.GetType().Name);
        }
        catch (Exception error)
        {
            CaptureRawgFailure(error);
            return Abstain("game_provider_or_publication_unavailable", 503,
                errorCategory: error is OperationCanceledException ? "cancellation" : stage == "publication" ? "publication_error" : "provider_error",
                exceptionType: error.GetType().Name);
        }
    }
}
