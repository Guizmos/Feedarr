using System.Reflection;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Tmdb;

internal sealed record PlannedQuery(string Provider, string MediaType, string Title, int? Year, int Limit);
internal sealed record RecordedHit(ProviderSearchCandidate Hit, bool HasPoster = false);
internal sealed record HistoricalDecisionContext(int? Season, int? Episode);

// Only static pure helpers are invoked; never instantiate PosterFetchService or an API host.
internal sealed partial class HistoricalAdapter
{
    private static readonly Type Core = typeof(PosterFetchService);
    private static readonly BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly MethodInfo Clean = Core.GetMethods(Flags).Single(m =>
        m.Name.Contains("g__CleanTitle|") && m.ReturnType == typeof(string) && m.GetParameters().Length == 1);
    private static readonly MethodInfo Game = Core.GetMethod("QuerySanitizeGame", Flags)!;
    private static readonly MethodInfo Scores = Core.GetMethod("ScoreCandidates", Flags)!;
    private static readonly MethodInfo Dedup = Core.GetMethod("DedupCandidates", Flags)!;
    private static readonly float TmdbCommonTitleThreshold = (float)Core.GetField("TmdbCommonTitleThreshold", Flags)!.GetRawConstantValue()!;
    private static readonly int TmdbCandidateLimit = (int)Core.GetField("TmdbCandidateLimit", Flags)!.GetRawConstantValue()!;
    internal static string CleanStoredTitle(string title) => (string)Clean.Invoke(null, new object[] { title })!;
    internal static string GameQuery(string title) => (string)Game.Invoke(null, new object[] { title })!;
    private static List<(TmdbClient.SearchResult Candidate, float Score)> ScoreCandidates(string title, int? year,
        Feedarr.Api.Models.UnifiedCategory category, IEnumerable<TmdbClient.SearchResult> candidates, bool requirePoster)
        => (List<(TmdbClient.SearchResult, float)>)Scores.Invoke(null, new object?[] { title, year, category, candidates, requirePoster })!;
    private static List<TmdbClient.SearchResult> DedupCandidates(IEnumerable<TmdbClient.SearchResult> candidates)
        => (List<TmdbClient.SearchResult>)Dedup.Invoke(null, new object[] { candidates })!;
    internal static List<TmdbClient.SearchResult> DeduplicateObserved(IEnumerable<TmdbClient.SearchResult> candidates) => DedupCandidates(candidates);
    private readonly QuerySink _tmdb = new();
    private static Task<T> RunTmdbAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) => operation(ct);
    internal async Task<IReadOnlyList<PlannedQuery>> TmdbPlan(string title, int? year, string type, bool tvOnly)
    {
        _tmdb.Queries.Clear();
        await GetTmdbCandidatesAsync(title, year, type, CancellationToken.None, tvOnly);
        return _tmdb.Queries.ToArray();
    }
    private sealed class QuerySink
    {
        internal List<PlannedQuery> Queries { get; } = new();
        public Task<List<TmdbClient.SearchResult>> SearchMovieListAsync(string title, int? year, CancellationToken ct, int limit)
            => Record("movie", title, year, limit);
        public Task<List<TmdbClient.SearchResult>> SearchTvListAsync(string title, int? year, CancellationToken ct, int limit)
            => Record("series", title, year, limit);
        private Task<List<TmdbClient.SearchResult>> Record(string type, string title, int? year, int limit)
        {
            Queries.Add(new("tmdb", type, title, year, limit));
            // Control-flow sink only. This empty collection is NOT a recorded provider
            // response and is never passed to either scoring/identification engine.
            return Task.FromResult(new List<TmdbClient.SearchResult>());
        }
    }
}
