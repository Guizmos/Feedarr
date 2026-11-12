using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Posters;

public sealed partial class PosterFetchService
{
    public MatchingShadowDiagnostics ShadowDiagnostics { get; }
    internal IReadOnlyList<MatchingShadowObservation> MatchingShadowObservations => ShadowDiagnostics.Observations;

    private void ObserveMatchingShadow(long releaseId, HistoricalShadowDecision historical, MatchingShadowBatch? batch = null,
        long? capturedGeneration = null, bool captured = false)
    {
        var generation = captured ? capturedGeneration : ShadowDiagnostics.TryBegin();
        if (!generation.HasValue || !ShadowDiagnostics.IsCurrent(generation.Value)) return;
        try
        {
            var expected = _releases.MatchingSnapshotForShadow;
            if (expected is null) return;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var observation = MatchingShadowEvaluator.Observe(true, MatchingShadowState.From(expected),
                () => _releases.ReadMatchingShadowState(releaseId), batch, historical);
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (observation is not null)
                ShadowDiagnostics.Publish(generation.Value, observation, expected.Title, batch, elapsed);
        }
        catch (Exception) { /* Observational failure cannot alter the historical path. */ }
    }

    private static HistoricalShadowDecision HistoricalTmdbChoice(TmdbClient.SearchResult? selected, double score)
    {
        var type = TmdbIdentity.NormalizeType(selected?.MediaType);
        return new(selected is null ? "Reject" : "Accept",
            selected is not null && type is not null ? new SearchIdentity("tmdb", type == "movie" ? SearchMediaType.Movie : SearchMediaType.Series,
                selected.TmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null,
            selected is null ? null : score);
    }
}
