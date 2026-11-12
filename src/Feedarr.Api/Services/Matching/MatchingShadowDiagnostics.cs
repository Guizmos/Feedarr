using System.Text.RegularExpressions;

namespace Feedarr.Api.Services.Matching;

public sealed record ShadowIdentity(string Provider, string Type, string Id);
public sealed record ShadowQuery(string Title, int? Year, string YearEvidence);
public sealed record ShadowOfficialTitle(string Title, string Kind, string Source, string? Language);
public sealed record ShadowCandidate(ShadowIdentity Identity, string Title, int? Year, double Confidence,
    string Decision, IReadOnlyList<string> Reasons, IReadOnlyList<string> Contradictions, IReadOnlyList<ShadowOfficialTitle>? OfficialTitles = null);
public sealed record ShadowEntry(long ReleaseId, string RawTitle, string HistoricalDecision, ShadowIdentity? HistoricalIdentity,
    double? HistoricalScore, string Decision, ShadowIdentity? SelectedIdentity, bool Divergence,
    double DurationMs, bool PreserveExisting, IReadOnlyList<string> Reasons,
    IReadOnlyList<ShadowQuery> Queries, IReadOnlyList<ShadowCandidate> Candidates);
public sealed record ShadowSnapshot(bool Enabled, long Generation, int Capacity, int Observations, int Releases,
    int Evaluated, int EvaluatedReleases, int Accept, int Ambiguous, int Reject, int NotEvaluable, int Divergences,
    double AverageDurationMs, IReadOnlyList<ShadowEntry> Entries);

/// <summary>Process-local switch and bounded window; no configuration persistence, provider or database writer.</summary>
public sealed class MatchingShadowDiagnostics
{
    public const int Capacity = 64;
    private readonly object _gate = new();
    private readonly Queue<(MatchingShadowObservation Observation, ShadowEntry Entry)> _entries = new();
    private bool _enabled; // Always OFF on process/service creation, including configured legacy App options.
    private long _generation;

    internal long? TryBegin() { lock (_gate) return _enabled ? _generation : null; }
    internal bool IsCurrent(long generation) { lock (_gate) return _enabled && generation == _generation; }
    public ShadowSnapshot SetEnabled(bool enabled, long? expectedGeneration = null)
    {
        lock (_gate)
        {
            if (enabled && expectedGeneration.HasValue && expectedGeneration != _generation) return SnapshotUnsafe();
            _enabled = enabled; _generation++;
            return SnapshotUnsafe();
        }
    }
    public ShadowSnapshot Reset()
    {
        lock (_gate) { _generation++; _entries.Clear(); return SnapshotUnsafe(); }
    }
    public ShadowSnapshot Snapshot() { lock (_gate) return SnapshotUnsafe(); }
    internal IReadOnlyList<MatchingShadowObservation> Observations
    { get { lock (_gate) return _entries.Select(e => e.Observation).ToArray(); } }

    internal void Publish(long generation, MatchingShadowObservation observation, string? rawTitle,
        MatchingShadowBatch? batch, double durationMs)
    {
        if (!IsCurrent(generation)) return;
        var entry = Project(observation, rawTitle, batch, durationMs);
        lock (_gate)
        {
            if (!_enabled || generation != _generation) return; // Stop/reset never waits for text projection.
            while (_entries.Count >= Capacity) _entries.Dequeue();
            _entries.Enqueue((observation, entry));
        }
    }

    internal static ShadowEntry Project(MatchingShadowObservation observation, string? rawTitle,
        MatchingShadowBatch? batch, double durationMs)
    {
        var result = observation.V2;
        var decision = result?.Decision.ToString().ToLowerInvariant() ?? "not_evaluable";
        var historical = observation.Historical;
        // A different identity is comparable only when the historical side actually accepted/rejected.
        var comparable = historical.Decision is "Accept" or "Reject" && result is not null;
        var divergence = comparable && (decision != historical.Decision.ToLowerInvariant() ||
            (result!.Decision == CandidateDecision.Accept && result.SelectedIdentity != historical.Identity));
        var candidates = result?.Candidates.Select(c =>
        {
            var row = batch?.Results?.FirstOrDefault(r => r.TmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture) == c.Identity.Id &&
                TmdbIdentity.NormalizeType(r.MediaType) == (c.Identity.MediaType == SearchMediaType.Movie ? "movie" : "tv"));
            return new ShadowCandidate(Identity(c.Identity)!, SafeText(row?.Title ?? observation.ProviderCandidates.FirstOrDefault(p => p.Identity == c.Identity)?.Title), row?.Year ?? observation.ProviderCandidates.FirstOrDefault(p => p.Identity == c.Identity)?.Year, c.Confidence,
                c.Decision.ToString().ToLowerInvariant(), c.PositiveReasons.ToArray(), c.Contradictions.Select(x => x.Code).ToArray(),
                observation.ProviderCandidates.Where(p => p.Identity == c.Identity).SelectMany(p => p.OfficialTitles ?? Array.Empty<OfficialProviderTitle>())
                    .Distinct().Select(t => new ShadowOfficialTitle(SafeText(t.Title), t.Kind.ToString(), t.Source.ToString(),
                        t.Language is null ? null : SafeText(t.Language)[..Math.Min(16, SafeText(t.Language).Length)])).ToArray());
        }).ToArray() ?? Array.Empty<ShadowCandidate>();
        var queries = observation.Queries.Select(q => new ShadowQuery(SafeText(q.Title), q.Year, q.YearEvidence.ToString())).ToArray();
        return new ShadowEntry(observation.ReleaseId, SafeText(rawTitle), historical.Decision,
            Identity(historical.Identity), historical.Confidence, decision, Identity(result?.SelectedIdentity), divergence,
            double.IsFinite(durationMs) ? Math.Max(0, durationMs) : 0, observation.PreserveExisting,
            observation.MissingInformation.Concat(result?.Reasons ?? Array.Empty<string>()).ToArray(), queries, candidates);
    }

    private ShadowSnapshot SnapshotUnsafe()
    {
        var entries = _entries.Select(e => e.Entry).Reverse().ToArray();
        return new(_enabled, _generation, Capacity, entries.Length, entries.Select(e => e.ReleaseId).Distinct().Count(),
            entries.Count(e => e.Decision != "not_evaluable"), entries.Where(e => e.Decision != "not_evaluable").Select(e => e.ReleaseId).Distinct().Count(),
            entries.Count(e => e.Decision == "accept"),
            entries.Count(e => e.Decision == "ambiguous"), entries.Count(e => e.Decision == "reject"),
            entries.Count(e => e.Decision == "not_evaluable"), entries.Count(e => e.Divergence),
            entries.Length == 0 ? 0 : entries.Average(e => e.DurationMs), entries);
    }
    private static ShadowIdentity? Identity(SearchIdentity? identity) => identity is null ? null :
        new(identity.Provider, identity.MediaType == SearchMediaType.Movie ? "movie" : identity.MediaType == SearchMediaType.Series ? "tv" : "unknown", identity.Id);

    // Only release/search titles are accepted; never project tracker/source objects, HTTP requests or configuration.
    // Hide the entire value if it resembles a URL, credential, path or email; React renders returned text as text only.
    internal static string SafeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (Regex.IsMatch(value, @"(?i)(https?://|www\.|[/\\]|@|token|passkey|api[_-]?key|bearer|password|secret|\b[\w-]+\.(?:com|net|org|fr|io|tv|me|xyz)\b)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20)))
            return "[titre masqué : contenu sensible possible]";
        return new string(value.Where(c => !char.IsControl(c)).Take(512).ToArray());
    }
}
