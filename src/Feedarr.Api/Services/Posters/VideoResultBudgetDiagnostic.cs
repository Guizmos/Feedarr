using Feedarr.Api.Models;

namespace Feedarr.Api.Services.Posters;

internal sealed record VideoResultBudgetCounters(int SearchQueries, int SearchRequests, int PagesReceived,
    int ObservedResults, int? Page = null, int? TotalPages = null, int? TotalResults = null,
    int? ProjectedCandidates = null);

internal sealed record VideoResultBudgetDiagnostic(string Reason, string LimitName, int Count, int Limit,
    VideoResultBudgetCounters Counters)
{
    // Keep the public abstention contract; only the recorded diagnosis becomes more precise.
    internal const string AbstentionReason = "not_evaluable_result_budget";

    internal static VideoResultBudgetDiagnostic? CheckSearchPages(int requiredPages, int limit,
        VideoResultBudgetCounters counters)
        => requiredPages > limit
            ? new("not_evaluable_search_budget", "search_pages", requiredPages, limit, counters)
            : null;

    internal static VideoResultBudgetDiagnostic? CheckProjection(int projectedCandidates, int limit,
        VideoResultBudgetCounters counters)
        => projectedCandidates > limit
            ? new("not_evaluable_projected_candidate_budget", "projected_candidates", projectedCandidates, limit,
                counters with { ProjectedCandidates = projectedCandidates })
            : null;

    internal void Log(ILogger logger, long releaseId, UnifiedCategory category)
    {
        // Numeric counters and controlled codes only: no title, URL, credential or response payload.
        logger.LogWarning("Matching V2 result budget releaseId={ReleaseId} category={Category} stage={Stage} diagnosticReason={DiagnosticReason} limitName={LimitName} count={Count} limit={Limit} searchQueries={SearchQueries} searchRequests={SearchRequests} pagesReceived={PagesReceived} page={Page} totalPages={TotalPages} totalResults={TotalResults} projectedCandidates={ProjectedCandidates} observedResults={ObservedResults}",
            releaseId, category, "collection", Reason, LimitName, Count, Limit,
            Counters.SearchQueries, Counters.SearchRequests, Counters.PagesReceived, Counters.Page,
            Counters.TotalPages, Counters.TotalResults, Counters.ProjectedCandidates, Counters.ObservedResults);
    }
}
