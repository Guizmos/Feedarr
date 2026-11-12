using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Posters;

public sealed partial class PosterFetchService
{
    private readonly long _movieV2CohortMinimum;

    private bool UseMovieV2(ReleaseForPoster snapshot, UnifiedCategory category)
        => _opt.MatchingV2MoviesEnabled && category == UnifiedCategory.Film &&
           snapshot.Id >= Math.Max(_movieV2CohortMinimum, _opt.MatchingV2MoviesMinReleaseId);

    internal async Task<PosterFetchResult> FetchMovieV2Async(PosterFetchRoutingContext context, CancellationToken ct)
    {
        PosterFetchResult Abstain(string reason, int status = 422)
        {
            _logger.LogInformation("Matching V2 movie abstained releaseId={ReleaseId} reason={Reason}", context.ReleaseId, reason);
            return new(false, status, new { error = "matching_v2_abstained", reason }, context.SourceId);
        }
        try
        {
            var current = _releases.ReadMatchingShadowState(context.ReleaseId);
            if (current is null || current.Protected) return Abstain("protected_or_unavailable_identity", 409);
            _releases.ValidateMatching(context.ReleaseId);
            if (_opt.MatchingV2MoviesSearchBudget is < 1 or > 12 || _opt.MatchingV2MoviesMaxPagesPerQuery is < 1 or > 3)
                return Abstain("invalid_search_budget");
            var queries = SearchCandidateGenerator.Generate(new(current.RawTitle, UnifiedCategory.Film));
            if (queries.Count == 0) return Abstain("no_query");
            // Explicitly bounded plan: every generated title, configured locale + English official search fields,
            // unfiltered years, and every page. Any uncovered part abstains, even after an excellent hit.
            var observed = new List<TmdbClient.ObservedSearchResult>();
            var used = 0;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            foreach (var title in queries.Select(q => q.Title).Distinct(StringComparer.Ordinal))
            foreach (var language in _tmdb.MovieMatchingLanguages)
            {
                var pages = 1;
                int? total = null;
                var ids = new HashSet<int>();
                for (var page = 1; page <= pages; page++)
                {
                    if (++used > _opt.MatchingV2MoviesSearchBudget) return Abstain("not_evaluable_search_budget");
                    var response = await RunTmdbAsync(inner => _tmdb.SearchMovieMatchingPageAsync(title, language, page, inner), timeout.Token).ConfigureAwait(false);
                    if (response is null) return Abstain("not_evaluable_provider_coverage");
                    if (total.HasValue && (response.TotalResults != total || response.TotalPages != pages))
                        return Abstain("not_evaluable_provider_changed_during_pagination");
                    total = response.TotalResults;
                    pages = response.TotalPages;
                    if (pages > _opt.MatchingV2MoviesMaxPagesPerQuery) return Abstain("not_evaluable_page_budget");
                    foreach (var result in response.Results)
                    {
                        if (!ids.Add(result.TmdbId)) return Abstain("not_evaluable_duplicate_page_identity");
                        observed.Add(new(result, language));
                    }
                }
            }
            var projected = TmdbShadowCandidateAdapter.Project(observed);
            if (projected.Length > new CandidateRankingPolicy().MaximumResults) return Abstain("not_evaluable_result_budget");
            var ranked = SearchCandidateRanker.Rank(new(queries, UnifiedCategory.Film, SearchMediaType.Movie,
                Season: current.Season, Episode: current.Episode), projected);
            if (ranked.Decision != CandidateDecision.Accept || ranked.SelectedIdentity is not { } identity)
                return Abstain(ranked.Decision.ToString().ToLowerInvariant());
            if (_releases.ReadMatchingShadowState(context.ReleaseId) != current) return Abstain("identity_changed", 409);
            _releases.ValidateMatching(context.ReleaseId);
            var selected = observed.Where(r => r.Result.TmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture) == identity.Id)
                .OrderByDescending(r => !string.IsNullOrWhiteSpace(r.Result.PosterPath)).First().Result;
            var confidence = (float)ranked.Candidates.First(r => r.Identity == identity).Confidence;
            _logger.LogInformation("Matching V2 movie accepted releaseId={ReleaseId} identity=tmdb:movie:{TmdbId} confidence={Confidence} searchRequests={SearchRequests}",
                context.ReleaseId, identity.Id, confidence, used);
            // One identity only. Preparation/publication is isolated from the legacy decision path;
            // missing mandatory TMDB evidence abstains rather than choosing another work/provider.
            return await PublishMovieV2Async(context, selected, timeout.Token).ConfigureAwait(false);
        }
        catch (ReleaseRepository.StaleMatchException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // No URLs, provider responses or credentials in diagnostics; no historical fallback.
            return Abstain("not_evaluable_provider_or_publication_error", 503);
        }
    }
}
