using System.Diagnostics;
using System.Text.Json;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Matching.Replay;

public sealed record ReplayRequest(int Count = 10, string Mode = "offline", int Budget = 30, long Generation = 0, ReplaySelection? Selection = null, IReadOnlyList<long>? ExpectedIds = null);
public sealed record ReplayDistribution(string Category, int Count, int WithoutPoster, int WithoutIdentity, int Protected);
public sealed record ReplayEntry(string Category, bool HasPoster, bool Protected, string IdentityVerified,
    bool? DecisionDifference, bool? IdentityDifference, int? ParsedYear, int? Season, int? Episode, string Coverage, int Requests, bool Error, ShadowEntry Detail, string? ProviderEvidenceFingerprint = null,
    bool ProviderEvidenceComplete = false, string? QueryPlanFingerprint = null, string? SearchLocale = null, int? ProviderEvidenceVersion = null);
public sealed record ReplaySummary(int Accept, int Ambiguous, int Reject, int NotEvaluable, int Divergences, double AverageDurationMs);
public sealed record ReplaySnapshot(Guid? SessionId, long Generation, string Status, string Mode, int Requested, int Total,
    int Processed, int Errors, int RequestsUsed, int RequestBudget, bool Running, string? SessionError,
    IReadOnlyList<ReplayDistribution> Distribution, ReplaySummary Summary, IReadOnlyList<ReplayEntry> Entries,
    ReplaySelection? Selection = null, IReadOnlyList<long>? SelectedIds = null, IReadOnlyList<long>? ProcessedIds = null,
    IReadOnlyList<ReplayCategoryBudget>? CategoryBudgets = null, int ReportVersion = 2);

public sealed class MatchingReplayService(MatchingReplayReader reader, IHttpClientFactory clients,
    MatchingShadowDiagnostics shadow, IHostApplicationLifetime lifetime) : IDisposable
{
    private readonly object _gate = new();
    private long _generation;
    private Session? _session;
    private Task? _worker;
    private bool _disposed;
    private int _planning;
    private DateTimeOffset _retryAfterUtc; // Shared across sessions/reset, never persisted or applied to production.
    private sealed class Session(ReplayRequest request, long generation, CancellationToken shutdown, ReplayPreview preview, IReadOnlyList<ReplayRelease> releases)
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal ReplayRequest Request { get; } = request;
        internal ReplayPreview Preview { get; } = preview;
        internal IReadOnlyList<ReplayRelease> Releases { get; } = releases;
        internal Dictionary<string, ReplayHttpBudget> CategoryBudgets { get; } = new(StringComparer.Ordinal);
        internal long Generation { get; } = generation;
        internal CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        internal ReplayHttpBudget Budget { get; } = new(request.Budget);
        internal List<ReplayEntry> Entries { get; } = new();
        internal IReadOnlyList<ReplayDistribution> Distribution { get; set; } = preview.Distribution;
        internal IReadOnlyList<MatchingShadowObservation> OfflineObservations { get; set; } = Array.Empty<MatchingShadowObservation>();
        internal int Total { get; set; } = releases.Count;
        internal string Status { get; set; } = "preparing";
        internal string? Error { get; set; }
    }
    public ReplaySnapshot Snapshot() { lock (_gate) return SnapshotUnsafe(); }
    public ReplayPreview Preview(ReplayRequest request) => Plan(ReplaySelectionRules.Normalize(request)).Preview;
    private (ReplayPreview Preview, IReadOnlyList<ReplayRelease> Releases) Plan(ReplayRequest request)
    {
        if (Interlocked.CompareExchange(ref _planning, 1, 0) != 0) throw new InvalidOperationException("replay_selection_busy");
        try { return PlanCore(request); }
        finally { Volatile.Write(ref _planning, 0); }
    }
    private (ReplayPreview Preview, IReadOnlyList<ReplayRelease> Releases) PlanCore(ReplayRequest request)
    {
        var shadowIds = request.Selection!.Method == "shadow_divergences"
            ? shadow.Snapshot().Entries.Where(e => e.Divergence).Select(e => e.ReleaseId).Distinct().Order().ToArray() : Array.Empty<long>();
        var releases = reader.Select(request, shadowIds);
        var distribution = releases.GroupBy(r => MatchingReplayReader.CanonicalCategory(r.State.Category))
            .Select(g => new ReplayDistribution(g.Key, g.Count(), g.Count(r => !r.HasPoster),
                g.Count(r => !HasIdentity(r.State)), g.Count(r => r.State.Protected))).ToArray();
        var minimum = distribution.ToDictionary(g => g.Category, g => request.Mode == "offline" ? 0 :
            releases.Where(r => MatchingReplayReader.CanonicalCategory(r.State.Category) == g.Category).Sum(r => MinimumCalls(r.State)));
        var allocation = ReplaySelectionRules.Allocate(distribution, minimum, request.Budget, request.Selection.FairBudget);
        long generation; lock (_gate) generation = _generation;
        return (new(generation, request.Selection, releases.Select(r => r.State.Id).ToArray(), distribution, minimum.Values.Sum(), request.Budget,
            request.Mode == "tmdb" && minimum.Values.Sum() > 0, minimum.Values.Sum() > request.Budget || allocation.Any(a => a.Limit.HasValue && a.MinimumRequests > a.Limit), allocation), releases);
    }
    private static bool SensitiveQuery(string? raw) => System.Text.RegularExpressions.Regex.IsMatch(raw ?? "",
        @"(?i)(https?://|www\.|@|(?:api[_-]?key|token|passkey|password)\s*[=:]|bearer\s+\S+)");
    private static int MinimumCalls(MatchingShadowState state)
    {
        if (SensitiveQuery(state.RawTitle) || !(UnifiedCategoryMappings.TryParse(state.Category, out var category) || UnifiedCategoryMappings.TryParseKey(state.Category, out category))) return 0;
        return SearchCandidateGenerator.Generate(new(state.RawTitle, category)).Count(q => q.Title.Length <= 200) * 2;
    }
    public ReplaySnapshot Start(ReplayRequest request)
    {
        request = ReplaySelectionRules.Normalize(request);
        void Check()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (request.Generation != _generation || _worker is { IsCompleted: false }) throw new InvalidOperationException("replay_busy_or_state_changed");
        }
        lock (_gate) Check();
        var plan = Plan(request);
        if (request.ExpectedIds is not null && !request.ExpectedIds.SequenceEqual(plan.Preview.SelectedIds)) throw new InvalidOperationException("replay_selection_changed");
        // SQLite and parsing outside the service lock; repeat the generation check before starting.
        lock (_gate)
        {
            Check();
            var session = new Session(request, ++_generation, lifetime.ApplicationStopping, plan.Preview, plan.Releases);
            if (request.Selection!.FairBudget)
                foreach (var allocation in plan.Preview.CategoryBudgets)
                    session.CategoryBudgets.Add(allocation.Category, new(allocation.Limit!.Value, parent: session.Budget));
            _session = session;
            _worker = Task.Run(() => RunAsync(session));
            return SnapshotUnsafe();
        }
    }
    public ReplaySnapshot Stop(bool reset = false, Guid? expectedSessionId = null, long? expectedGeneration = null)
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if ((expectedSessionId.HasValue && _session?.Id != expectedSessionId) ||
                (expectedGeneration.HasValue && _generation != expectedGeneration))
                throw new InvalidOperationException("replay_session_changed");
            ++_generation; // Also invalidates a delayed start when already idle.
            cancellation = _session?.Cancellation;
            if (_session is not null) _session.Status = "stopped";
            if (reset) _session = null;
        }
        // Cancellation callbacks / network operations never run while holding the service lock.
        if (cancellation is not null) _ = cancellation.CancelAsync();
        return Snapshot();
    }
    public void Dispose() { lock (_gate) _disposed = true; Stop(); }
    private bool Current(Session session) => !_disposed && ReferenceEquals(_session, session) && session.Generation == _generation;
    private async Task RunAsync(Session session)
    {
        var ct = session.Cancellation.Token;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (session.Request.Mode == "offline") session.OfflineObservations = shadow.Observations;
            var releases = session.Releases;
            lock (_gate) { if (!Current(session)) return; session.Status = "running"; }
            using var http = session.Request.Mode == "tmdb" ? clients.CreateClient("tmdb-matching-replay") : null;
            string? providerHalted = null;
            foreach (var release in releases)
            {
                ct.ThrowIfCancellationRequested();
                lock (_gate) if (!Current(session)) return;
                var watch = Stopwatch.StartNew();
                var requestsBefore = session.Budget.Used;
                ReplayEntry entry;
                try
                {
                    var result = await EvaluateAsync(release.State, session, http, providerHalted, ct).ConfigureAwait(false);
                    if (result.Reason is "provider_rate_limited" or "provider_authentication_failed" or "tmdb_not_configured") providerHalted = result.Reason;
                    var detail = MatchingShadowDiagnostics.Project(result.Observation, release.State.RawTitle, null, watch.Elapsed.TotalMilliseconds);
                    var decisionDifference = result.Observation.Historical.Decision is "Accept" or "Reject" && result.Observation.V2 is not null
                        ? (bool?)(detail.Decision != result.Observation.Historical.Decision.ToLowerInvariant()) : null;
                    var identityDifference = detail.HistoricalIdentity is not null && detail.SelectedIdentity is not null
                        ? (bool?)(detail.HistoricalIdentity != detail.SelectedIdentity) : null;
                    detail = detail with { Divergence = decisionDifference == true || identityDifference == true };
                    entry = new(MatchingReplayReader.CanonicalCategory(release.State.Category), release.HasPoster, release.State.Protected,
                        "unknown", decisionDifference, identityDifference, release.State.Year, release.State.Season, release.State.Episode, result.Reason ?? "complete",
                        session.Budget.Used - requestsBefore, result.Error, detail, result.Fingerprint, result.Complete, result.QueryPlan, result.Locale, result.Fingerprint is null ? null : 1);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    entry = new(MatchingReplayReader.CanonicalCategory(release.State.Category), release.HasPoster, release.State.Protected,
                        "unknown", null, null, release.State.Year, release.State.Season, release.State.Episode, "diagnostic_read_or_evaluation_failed", session.Budget.Used - requestsBefore, true,
                        MatchingShadowDiagnostics.Project(Missing(release.State, "diagnostic_read_or_evaluation_failed"), release.State.RawTitle, null, watch.Elapsed.TotalMilliseconds));
                }
                ct.ThrowIfCancellationRequested();
                lock (_gate) { if (!Current(session)) return; session.Entries.Add(entry); }
            }
            lock (_gate) if (Current(session)) session.Status = "completed";
        }
        catch (OperationCanceledException) { lock (_gate) if (Current(session)) session.Status = "stopped"; }
        catch (Exception)
        {
            lock (_gate) if (Current(session)) { session.Status = "failed"; session.Error = "replay_selection_or_configuration_failed"; }
        }
    }
    private sealed record Evaluation(MatchingShadowObservation Observation, string? Reason = null, bool Error = false,
        string? Fingerprint = null, bool Complete = false, string? QueryPlan = null, string? Locale = null);
    private async Task<Evaluation> EvaluateAsync(MatchingShadowState expected, Session session, HttpClient? http, string? halted, CancellationToken ct)
    {
        IReadOnlyList<SearchCandidate> queries = Array.Empty<SearchCandidate>();
        var observed = new List<TmdbClient.ObservedSearchResult>();
        string? planFingerprint = null, locale = null;
        Evaluation NotEvaluable(string reason, bool error = false) => new(Missing(expected, reason) with { Queries = queries }, reason, error,
            observed.Count == 0 ? null : ReplayEvidence.Provider(TmdbShadowCandidateAdapter.Project(observed)), false, planFingerprint, locale);
        if (reader.Read(expected.Id) != expected) return NotEvaluable("persisted_state_changed");
        if (!(UnifiedCategoryMappings.TryParse(expected.Category, out var category) || UnifiedCategoryMappings.TryParseKey(expected.Category, out category)))
            return NotEvaluable("category_unsupported");
        var type = TmdbIdentity.NormalizeType(UnifiedCategoryMappings.ToMediaType(category));
        if (type is null) return NotEvaluable("media_type_unsupported");
        if (http is not null && SensitiveQuery(expected.RawTitle))
            return NotEvaluable("query_contains_sensitive_content");
        // Explicit diagnostic replay: IDs are context only, never query input or truth.
        queries = SearchCandidateGenerator.Generate(new(expected.RawTitle, category));
        if (queries.Count == 0) return NotEvaluable("no_query_generated");
        planFingerprint = ReplayEvidence.QueryPlan(queries, type);
        IReadOnlyList<ProviderSearchCandidate> candidates;
        var historical = Historical(expected);
        if (http is null)
        {
            var observation = session.OfflineObservations.LastOrDefault(o => o.ReleaseId == expected.Id && o.InputState == expected && o.Status == "evaluated");
            if (observation is null) return new(Missing(expected, "offline_provider_responses_unavailable") with { Queries = queries }, "offline_provider_responses_unavailable");
            candidates = observation.ProviderCandidates;
            historical = observation.Historical;
            var locales = candidates.SelectMany(c => c.OfficialTitles ?? Array.Empty<OfficialProviderTitle>()).Where(t => t.Kind == ProviderTitleKind.Localized)
                .Select(t => t.Language).Where(l => l is not null).Distinct(StringComparer.Ordinal).ToArray();
            locale = locales.Length == 1 ? ReplayEvidence.SafeLocale(locales[0]) : null;
        }
        else
        {
            if (halted is not null) return NotEvaluable(halted);
            if (_retryAfterUtc > DateTimeOffset.UtcNow) return NotEvaluable("provider_rate_limit_cooldown");
            var access = reader.ReadTmdbAccess();
            if (access is null) return NotEvaluable("tmdb_not_configured");
            locale = ReplayEvidence.SafeLocale(access.Language);
            var queryBudget = session.CategoryBudgets.GetValueOrDefault(MatchingReplayReader.CanonicalCategory(expected.Category)) ?? session.Budget;
            if (session.Request.Selection!.FairBudget && queryBudget.Maximum - queryBudget.Used < MinimumCalls(expected))
                return NotEvaluable("category_http_budget_insufficient");
            try
            {
                foreach (var query in queries)
                foreach (var language in new string?[] { null, access.Language })
                {
                    int? expectedPages = null, expectedTotal = null;
                    var found = new HashSet<int>();
                    for (var page = 1; ; page++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var response = await TmdbClient.SearchReplayPageAsync(http, access, query.Title, query.Year, type, language, page, queryBudget, ct).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        if (response.Error == "provider_rate_limited") _retryAfterUtc = response.RetryAfterUtc ?? DateTimeOffset.UtcNow.AddSeconds(60);
                        if (response.Error is not null) return NotEvaluable(response.Error, true);
                        if ((expectedPages.HasValue && expectedPages != response.TotalPages) || (expectedTotal.HasValue && expectedTotal != response.TotalResults))
                            return NotEvaluable("provider_pagination_inconsistent");
                        expectedPages = response.TotalPages; expectedTotal = response.TotalResults;
                        foreach (var row in response.Results)
                        {
                            if (!found.Add(row.Result.TmdbId)) return NotEvaluable("provider_pagination_duplicate");
                            observed.Add(row);
                        }
                        if (observed.Count > 100) return NotEvaluable("provider_result_limit_exceeded");
                        if (page >= response.TotalPages)
                        {
                            if (found.Count != response.TotalResults) return NotEvaluable("provider_response_partial");
                            break;
                        }
                    }
                }
            }
            catch (ReplayCategoryBudgetExceededException) { return NotEvaluable("category_http_budget_exhausted"); }
            catch (ReplayBudgetExceededException) { return NotEvaluable("http_budget_exhausted"); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return NotEvaluable("provider_timeout", true); }
            catch (IOException) { return NotEvaluable("provider_network_error", true); }
            catch (HttpRequestException) { return NotEvaluable("provider_network_error", true); }
            catch (JsonException) { return NotEvaluable("provider_response_invalid", true); }
            candidates = TmdbShadowCandidateAdapter.Project(observed);
        }
        var ranked = SearchCandidateRanker.Rank(new(queries, category, type == "movie" ? SearchMediaType.Movie : SearchMediaType.Series,
            Season: expected.Season, Episode: expected.Episode), candidates);
        ct.ThrowIfCancellationRequested();
        if (reader.Read(expected.Id) != expected) return NotEvaluable("persisted_state_changed_during_evaluation");
        return new(new(expected.Id, "evaluated", historical, ranked with { PreserveExisting = expected.Protected }, Array.Empty<string>(), expected.Protected)
            { Queries = queries, ProviderCandidates = candidates, InputState = expected }, http is null ? "offline_shadow_snapshot_complete" : null, Fingerprint: ReplayEvidence.Provider(candidates), Complete: true, QueryPlan: planFingerprint, Locale: locale);
    }
    private static bool HasIdentity(MatchingShadowState state) => state.TmdbId is > 0 || state.TvdbId is > 0 || !string.IsNullOrWhiteSpace(state.ExternalId);
    private static HistoricalShadowDecision Historical(MatchingShadowState state)
    {
        var type = TmdbIdentity.NormalizeType(state.TmdbType);
        var identity = state.TmdbId is > 0 && type is not null ? new SearchIdentity("tmdb", type == "movie" ? SearchMediaType.Movie : SearchMediaType.Series, state.TmdbId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null;
        return new(identity is null ? "historical_decision_unknown" : "stored_identity_only", identity, null);
    }
    private static MatchingShadowObservation Missing(MatchingShadowState state, string reason) =>
        new(state.Id, "not_evaluable", Historical(state), null, new[] { reason }, state.Protected) { InputState = state };
    private ReplaySnapshot SnapshotUnsafe()
    {
        var s = _session;
        var entries = s?.Entries.ToArray() ?? Array.Empty<ReplayEntry>();
        var summary = new ReplaySummary(entries.Count(e => e.Detail.Decision == "accept"), entries.Count(e => e.Detail.Decision == "ambiguous"),
            entries.Count(e => e.Detail.Decision == "reject"), entries.Count(e => e.Detail.Decision == "not_evaluable"),
            entries.Count(e => e.DecisionDifference == true || e.IdentityDifference == true), entries.Length == 0 ? 0 : entries.Average(e => e.Detail.DurationMs));
        return new(s?.Id, _generation, s?.Status ?? "idle", s?.Request.Mode ?? "offline", s is null ? 0 : s.Request.Selection!.Method == "ids" ? s.Request.Selection.ReleaseIds!.Count : s.Request.Count,
            s?.Total ?? 0, entries.Length, entries.Count(e => e.Error), s?.Budget.Used ?? 0, s?.Request.Budget ?? 30,
            _worker is { IsCompleted: false }, s?.Error, s?.Distribution ?? Array.Empty<ReplayDistribution>(), summary, entries, s?.Request.Selection,
            s?.Releases.Select(r => r.State.Id).ToArray() ?? Array.Empty<long>(), entries.Select(e => e.Detail.ReleaseId).ToArray(),
            s?.Preview.CategoryBudgets.Select(a => a with { Used = s.CategoryBudgets.TryGetValue(a.Category, out var quota) ? quota.Used : entries.Where(e => e.Category == a.Category).Sum(e => e.Requests) }).ToArray() ?? Array.Empty<ReplayCategoryBudget>());
    }
}
