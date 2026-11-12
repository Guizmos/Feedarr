using System.Diagnostics;
using System.Text.Json;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Matching.Replay;

public sealed record ReplayRequest(int Count = 10, string Mode = "offline", int Budget = 30, long Generation = 0);
public sealed record ReplayDistribution(string Category, int Count, int WithoutPoster, int WithoutIdentity, int Protected);
public sealed record ReplayEntry(string Category, bool HasPoster, bool Protected, string IdentityVerified,
    bool? DecisionDifference, bool? IdentityDifference, int? ParsedYear, int? Season, int? Episode, string Coverage, int Requests, bool Error, ShadowEntry Detail);
public sealed record ReplaySummary(int Accept, int Ambiguous, int Reject, int NotEvaluable, int Divergences, double AverageDurationMs);
public sealed record ReplaySnapshot(Guid? SessionId, long Generation, string Status, string Mode, int Requested, int Total,
    int Processed, int Errors, int RequestsUsed, int RequestBudget, bool Running, string? SessionError,
    IReadOnlyList<ReplayDistribution> Distribution, ReplaySummary Summary, IReadOnlyList<ReplayEntry> Entries);

public sealed class MatchingReplayService(MatchingReplayReader reader, IHttpClientFactory clients,
    MatchingShadowDiagnostics shadow, IHostApplicationLifetime lifetime) : IDisposable
{
    private readonly object _gate = new();
    private long _generation;
    private Session? _session;
    private Task? _worker;
    private bool _disposed;
    private DateTimeOffset _retryAfterUtc; // Shared across sessions/reset, never persisted or applied to production.
    private sealed class Session(ReplayRequest request, long generation, CancellationToken shutdown)
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal ReplayRequest Request { get; } = request;
        internal long Generation { get; } = generation;
        internal CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        internal ReplayHttpBudget Budget { get; } = new(request.Budget);
        internal List<ReplayEntry> Entries { get; } = new();
        internal IReadOnlyList<ReplayDistribution> Distribution { get; set; } = Array.Empty<ReplayDistribution>();
        internal IReadOnlyList<MatchingShadowObservation> OfflineObservations { get; set; } = Array.Empty<MatchingShadowObservation>();
        internal int Total { get; set; }
        internal string Status { get; set; } = "preparing";
        internal string? Error { get; set; }
    }
    public ReplaySnapshot Snapshot() { lock (_gate) return SnapshotUnsafe(); }
    public ReplaySnapshot Start(ReplayRequest request)
    {
        if (request.Count is not (10 or 25 or 50 or 100 or 200) || request.Mode is not ("offline" or "tmdb") || request.Budget is < 1 or > 30)
            throw new ArgumentException("replay_request_invalid");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (request.Generation != _generation || _worker is { IsCompleted: false }) throw new InvalidOperationException("replay_busy_or_state_changed");
            var session = new Session(request, ++_generation, lifetime.ApplicationStopping);
            _session = session;
            _worker = Task.Run(() => RunAsync(session));
            return SnapshotUnsafe();
        }
    }
    public ReplaySnapshot Stop(bool reset = false)
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
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
            var releases = reader.Select(session.Request.Count);
            lock (_gate)
            {
                if (!Current(session)) return;
                session.Total = releases.Count;
                session.Distribution = releases.GroupBy(r => MatchingReplayReader.CanonicalCategory(r.State.Category))
                    .Select(g => new ReplayDistribution(g.Key, g.Count(), g.Count(r => !r.HasPoster),
                        g.Count(r => !HasIdentity(r.State)), g.Count(r => r.State.Protected))).ToArray();
                session.Status = "running";
            }
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
                        session.Budget.Used - requestsBefore, result.Error, detail);
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
    private sealed record Evaluation(MatchingShadowObservation Observation, string? Reason = null, bool Error = false);
    private async Task<Evaluation> EvaluateAsync(MatchingShadowState expected, Session session, HttpClient? http, string? halted, CancellationToken ct)
    {
        IReadOnlyList<SearchCandidate> queries = Array.Empty<SearchCandidate>();
        Evaluation NotEvaluable(string reason, bool error = false) => new(Missing(expected, reason) with { Queries = queries }, reason, error);
        if (reader.Read(expected.Id) != expected) return NotEvaluable("persisted_state_changed");
        if (!(UnifiedCategoryMappings.TryParse(expected.Category, out var category) || UnifiedCategoryMappings.TryParseKey(expected.Category, out category)))
            return NotEvaluable("category_unsupported");
        var type = TmdbIdentity.NormalizeType(UnifiedCategoryMappings.ToMediaType(category));
        if (type is null) return NotEvaluable("media_type_unsupported");
        if (http is not null && System.Text.RegularExpressions.Regex.IsMatch(expected.RawTitle ?? "",
            @"(?i)(https?://|www\.|@|(?:api[_-]?key|token|passkey|password)\s*[=:]|bearer\s+\S+)"))
            return NotEvaluable("query_contains_sensitive_content");
        // Explicit diagnostic replay: IDs are context only, never query input or truth.
        queries = SearchCandidateGenerator.Generate(new(expected.RawTitle, category));
        if (queries.Count == 0) return NotEvaluable("no_query_generated");
        IReadOnlyList<ProviderSearchCandidate> candidates;
        var historical = Historical(expected);
        if (http is null)
        {
            var observation = session.OfflineObservations.LastOrDefault(o => o.ReleaseId == expected.Id && o.InputState == expected && o.Status == "evaluated");
            if (observation is null) return new(Missing(expected, "offline_provider_responses_unavailable") with { Queries = queries }, "offline_provider_responses_unavailable");
            candidates = observation.ProviderCandidates;
            historical = observation.Historical;
        }
        else
        {
            if (halted is not null) return NotEvaluable(halted);
            if (_retryAfterUtc > DateTimeOffset.UtcNow) return NotEvaluable("provider_rate_limit_cooldown");
            var access = reader.ReadTmdbAccess();
            if (access is null) return NotEvaluable("tmdb_not_configured");
            var observed = new List<TmdbClient.ObservedSearchResult>();
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
                        var response = await TmdbClient.SearchReplayPageAsync(http, access, query.Title, query.Year, type, language, page, session.Budget, ct).ConfigureAwait(false);
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
            { Queries = queries, ProviderCandidates = candidates, InputState = expected }, http is null ? "offline_shadow_snapshot_complete" : null);
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
        return new(s?.Id, _generation, s?.Status ?? "idle", s?.Request.Mode ?? "offline", s?.Request.Count ?? 0,
            s?.Total ?? 0, entries.Length, entries.Count(e => e.Error), s?.Budget.Used ?? 0, s?.Request.Budget ?? 30,
            _worker is { IsCompleted: false }, s?.Error, s?.Distribution ?? Array.Empty<ReplayDistribution>(), summary, entries);
    }
}
