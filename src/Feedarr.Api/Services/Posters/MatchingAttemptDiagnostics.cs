using System.Text.Json;
using System.Text.RegularExpressions;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Services.Posters;

// Per-job mutable scope: async children share this bag, concurrent jobs never do.
// Observations only; none of these fields participate in a matching decision.
internal sealed class MatchingAttemptDiagnostics : IDisposable
{
    private static readonly AsyncLocal<MatchingAttemptDiagnostics?> Slot = new();
    internal static MatchingAttemptDiagnostics? Current => Slot.Value;
    private readonly MatchingAttemptDiagnostics? _previous;
    private readonly object _gate = new();
    private readonly SortedSet<string> _providers = new(StringComparer.Ordinal);
    private readonly List<object> _coverage = new();
    private int _transportRequests;
    internal int TransportRequests => Volatile.Read(ref _transportRequests);
    internal long ReleaseId { get; }
    internal DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    internal string Strategy { get; private set; } = "none";
    internal string Stage { get; private set; } = "routing";
    internal string? Reason { get; private set; }
    internal string? Ranking { get; private set; }
    private MatchingAttemptDiagnostics(long id) { ReleaseId = id; _previous = Slot.Value; Slot.Value = this; }
    internal static MatchingAttemptDiagnostics Begin(long id) => new(id);
    public void Dispose() => Slot.Value = _previous;
    internal void ProviderCalled(string provider) { Interlocked.Increment(ref _transportRequests); lock (_gate) _providers.Add(provider); }
    internal string Providers { get { lock (_gate) return _providers.Count == 0 ? "none" : string.Join('+', _providers); } }
    internal string Coverage { get { lock (_gate) return _coverage.Count == 0 ? "not_started_or_not_recorded" : JsonSerializer.Serialize(_coverage); } }
    internal void SetOutcome(string stage, string reason) { Stage = stage; Reason = reason; }
    internal void SetStrategy(string strategy) => Strategy = strategy;
    internal void Response(string provider, string source)
    {
        lock (_gate) _coverage.Add(new { Provider = provider, ResponseSource = source });
    }
    internal void Collection(string strategy, string outcome, int? requests = null, int? requiredPages = null)
    {
        if (strategy is "targeted_year" or "general" or "general_fallback") Strategy = strategy;
        lock (_gate) _coverage.Add(new { Strategy = strategy, Outcome = outcome, SearchRequests = requests, RequiredPages = requiredPages });
    }
    internal static void LogRanking(ILogger logger, long releaseId, CandidateRankingResult ranked)
    {
        // IDs and controlled codes only, never provider titles, overviews, URLs or credentials.
        static string SafeId(string id) => Regex.IsMatch(id, @"^\d{1,20}$|^[a-fA-F0-9]{8}(?:-[a-fA-F0-9]{4}){3}-[a-fA-F0-9]{12}$") ? id : "redacted";
        var diagnostic = JsonSerializer.Serialize(new {
            CandidateCount = ranked.Candidates.Count, Limit = 6, Truncated = ranked.Candidates.Count > 6,
            RankingDecision = ranked.Decision.ToString().ToLowerInvariant(), ranked.Reasons,
            Candidates = ranked.Candidates.Take(6).Select(c => new {
                Provider = c.Identity.Provider is "tmdb" or "tvmaze" or "igdb" or "rawg" or "jikan" or "musicbrainz" or "theaudiodb" or "googlebooks" or "openlibrary" or "comicvine" ? c.Identity.Provider : "redacted", MediaType = c.Identity.MediaType.ToString(), Id = SafeId(c.Identity.Id),
                c.Confidence, c.EvidenceScore, Decision = c.Decision.ToString().ToLowerInvariant(),
                PositiveReasons = c.PositiveReasons.Take(12), Contradictions = c.Contradictions.Take(12)
            })
        });
        if (Current is { } current) { current.Stage = "ranking"; current.Ranking = diagnostic; }
        logger.LogInformation("Matching V2 ranking releaseId={ReleaseId} attemptedAtUtc={AttemptedAtUtc} ranking={Ranking}",
            releaseId, DateTimeOffset.UtcNow, diagnostic);
    }
}

// This is the innermost HTTP handler, after SSRF/circuit/retry guards. Cache hits
// and calls blocked before transport do not masquerade as provider requests.
internal sealed class MatchingProviderRequestHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var provider = request.RequestUri?.Host.ToLowerInvariant() switch {
            "api.themoviedb.org" or "image.tmdb.org" => "tmdb",
            "api.igdb.com" or "id.twitch.tv" => "igdb",
            "api.rawg.io" or "media.rawg.io" => "rawg",
            "api.tvmaze.com" => "tvmaze",
            "webservice.fanart.tv" or "assets.fanart.tv" => "fanart",
            "api.jikan.moe" => "jikan",
            "theaudiodb.com" or "www.theaudiodb.com" => "theaudiodb",
            "www.googleapis.com" or "books.google.com" => "googlebooks",
            "comicvine.gamespot.com" => "comicvine",
            "musicbrainz.org" or "coverartarchive.org" => "musicbrainz",
            "openlibrary.org" or "covers.openlibrary.org" => "openlibrary",
            _ => "other_public_provider"
        };
        MatchingAttemptDiagnostics.Current?.ProviderCalled(provider);
        return base.SendAsync(request, ct);
    }
}
