using System.Text.Json;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Tmdb;
using Feedarr.Api.Services.TvMaze;

internal sealed record CaptureHit(ProviderSearchCandidate Hit, bool HasPoster, string? Released = null,
    long? FirstReleaseUnix = null, int? TvdbId = null);
internal sealed record CaptureBatch(string Provider, string RequestKey, List<CaptureHit> Hits, int PageSize);
internal sealed record HistoricalProviderDecision(string Decision, SearchIdentity? SelectedIdentity,
    double? Confidence, IReadOnlyList<string> Reasons, JsonElement? Assessments = null);
internal sealed record CaptureReplayInput(string Id, UnifiedCategory Category, string HistoricalTitle,
    int? HistoricalYear, int? HistoricalSeason, int? HistoricalEpisode, bool Protected, bool Manual,
    IReadOnlyList<SearchCandidate> Queries, IReadOnlyList<CaptureBatch> HistoricalBatches,
    IReadOnlyList<CaptureBatch> V2Batches, JsonElement Parsed, JsonElement ExistingObservation);

internal static class CaptureComparison
{
    internal static object Evaluate(CaptureReplayInput input, JsonSerializerOptions options)
    {
        var media = input.Category == UnifiedCategory.JeuWindows ? SearchMediaType.Game :
            input.Category is UnifiedCategory.Serie or UnifiedCategory.Emission ? SearchMediaType.Series : SearchMediaType.Movie;
        var ambiguity = TitleAmbiguityEvaluator.Evaluate(input.HistoricalTitle,
            media == SearchMediaType.Series ? "series" : media == SearchMediaType.Movie ? "movie" : "game", input.HistoricalYear);
        var historical = Historical(input, ambiguity, options);
        int? Number(string name) => input.Parsed.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        var context = new CandidateRankingContext(input.Queries, input.Category, media, Season: Number("Season"), Episode: Number("Episode"));
        var hits = input.V2Batches.SelectMany(b => b.Hits).Select(h => h.Hit)
            .DistinctBy(h => JsonSerializer.Serialize(h, options)).ToArray();
        var sufficient = hits.Length <= new CandidateRankingPolicy().MaximumResults;
        var modern = sufficient ? SearchCandidateRanker.Rank(context, hits) : null;
        // Diagnostic ranking is separate from the active guard. Existing IDs are
        // never injected as truth/hints, and no persisted identity can be replaced.
        var active = input.Protected || input.Manual ? SearchCandidateRanker.Rank(
            context with { IsLocked = input.Protected, HasManualCorrection = input.Manual }, Array.Empty<ProviderSearchCandidate>()) : modern;
        return new { input.Id, ProtectedDiagnosticOnly = input.Protected || input.Manual,
            Sufficient = sufficient, Historical = historical, V2 = modern, ActiveGuard = active,
            LimitReason = sufficient ? null : "ranking-input-limit-exceeded",
            HistoricalQueries = input.HistoricalBatches.Count, V2Queries = input.V2Batches.Count,
            HistoricalStage = "fresh-search-identity-selection-before-image-download-cache-and-configuration-not-replayed" };
    }

    private static HistoricalProviderDecision Historical(CaptureReplayInput input, TitleAmbiguityResult ambiguity, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(input.HistoricalTitle))
            return new("Reject", null, null, new[] { "historical-title-clean-missing-before-query" });
        if (input.Category == UnifiedCategory.JeuWindows)
        {
            var igdb = input.HistoricalBatches.Where(b => b.Provider == "igdb").SelectMany(b => b.Hits.Take(5)).ToList();
            var selected = HistoricalAdapter.AssessIgdb(igdb);
            if (selected.HasValue) return new("Accept", new("igdb", SearchMediaType.Game, selected.Value.igdbId.ToString()), null,
                new[] { "historical-igdb-first-cover-before-download" });
            var rawg = input.HistoricalBatches.Where(b => b.Provider == "rawg").SelectMany(b => b.Hits.Take(5)).ToList();
            var fallback = HistoricalAdapter.AssessRawg(rawg, input.HistoricalYear);
            return fallback.HasValue ? new("Accept", new("rawg", SearchMediaType.Game, fallback.Value.rawgId.ToString()), null,
                new[] { "historical-rawg-year-or-first-cover-before-download" }) : new("Reject", null, null, new[] { "historical-game-no-selection" });
        }
        var maze = input.HistoricalBatches.Where(b => b.Provider == "tvmaze").SelectMany(b => b.Hits.Take(10))
            .Select(c => new TvMazeClient.ShowResult(int.Parse(c.Hit.Identity.Id), c.Hit.Title, c.Hit.Year, null, c.TvdbId,
                null, c.HasPoster ? "observed-image-presence" : null)).ToList();
        if (input.HistoricalBatches.Any(b => b.Provider == "tvmaze"))
        {
            int? Observed(string name) => input.ExistingObservation.ValueKind == JsonValueKind.Object &&
                input.ExistingObservation.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
            var known = new PosterMatchIds(Observed("tmdbId"), Observed("tvdbId"), null, null, null);
            var first = HistoricalAdapter.AssessTvMaze(input.HistoricalTitle, input.HistoricalYear, input.Category, ambiguity, known, maze);
            if (first.SelectedIdentity is not null) return first;
        }
        var tmdb = input.HistoricalBatches.Where(b => b.Provider == "tmdb").SelectMany(b => b.Hits.Take(10))
            .Select(h => new TmdbClient.SearchResult(int.Parse(h.Hit.Identity.Id), h.Hit.Title,
                h.Hit.AlternativeTitles?.FirstOrDefault(), h.HasPoster ? "observed-image-presence" : null,
                h.Hit.Identity.MediaType == SearchMediaType.Movie ? "movie" : "tv", h.Hit.Year, null)).ToList();
        // Actual deduplication and actual scoring/policy/selection from the service.
        tmdb = HistoricalAdapter.DeduplicateObserved(tmdb);
        var legacy = JsonSerializer.SerializeToElement(HistoricalAdapter.Assess(input.HistoricalTitle, input.HistoricalYear,
            input.Category, ambiguity, tmdb, new(input.HistoricalSeason, input.HistoricalEpisode)), options);
        SearchIdentity? identity = legacy.GetProperty("selectedId").ValueKind == JsonValueKind.Number ?
            new("tmdb", legacy.GetProperty("selectedMediaType").GetString() == "movie" ? SearchMediaType.Movie : SearchMediaType.Series,
                legacy.GetProperty("selectedId").GetInt32().ToString()) : null;
        var selectedScore = legacy.GetProperty("assessments").EnumerateArray().FirstOrDefault(a => identity is not null &&
            a.GetProperty("id").GetInt32().ToString() == identity.Id &&
            a.GetProperty("type").GetString() == legacy.GetProperty("selectedMediaType").GetString());
        return new(legacy.GetProperty("decision").GetString()!, identity,
            selectedScore.ValueKind == JsonValueKind.Object ? selectedScore.GetProperty("score").GetDouble() : null,
            legacy.GetProperty("assessments").EnumerateArray().Select(a => a.GetProperty("decision").GetProperty("Reason").GetString()!).ToArray(),
            legacy.GetProperty("assessments").Clone());
    }
}
