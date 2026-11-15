using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Posters;

public sealed partial class PosterFetchService
{
    internal static bool UseVideoV2(ReleaseForPoster snapshot, UnifiedCategory category)
    {
        if (category is UnifiedCategory.Film or UnifiedCategory.Serie or UnifiedCategory.Emission) return true;
        if (category is not (UnifiedCategory.Animation or UnifiedCategory.Spectacle)) return false;
        // The category's default MediaType is not evidence: both categories map to movie.
        // Reuse the existing episodic/broadcast parser as a veto, never infer a movie
        // solely from the absence of an episode or from a release year.
        if (snapshot.Season.HasValue || snapshot.Episode.HasValue || string.IsNullOrWhiteSpace(snapshot.Title) ||
            TmdbIdentity.NormalizeType(snapshot.MediaType) == "tv" || TmdbIdentity.NormalizeType(snapshot.TmdbMediaType) == "tv") return false;
        var parser = new TitleParser();
        var episodic = parser.Parse(snapshot.Title, UnifiedCategory.Serie);
        if (episodic.Season.HasValue || episodic.Episode.HasValue ||
            parser.Parse(snapshot.Title, UnifiedCategory.Emission).AirDate is not null) return false;
        var standardIds = (snapshot.CategoryIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var id) ? id : 0)
            .Append(snapshot.StdCategoryId ?? 0).Append(snapshot.CategoryId ?? 0).Append(snapshot.SpecCategoryId ?? 0)
            .Where(CategorySelection.IsStandardById).ToArray();
        return standardIds.Length > 0 && standardIds.All(id => CategorySelection.ToStandardParentId(id) == 2000);
    }

    internal async Task<PosterFetchResult> FetchVideoV2Async(PosterFetchRoutingContext context, CancellationToken ct)
    {
        PosterFetchResult Abstain(string reason, int status = 422)
        {
            _logger.LogInformation("Matching V2 video abstained releaseId={ReleaseId} reason={Reason}", context.ReleaseId, reason);
            return new(false, status, new { error = "matching_v2_abstained", reason }, context.SourceId);
        }
        try
        {
            var current = _releases.ReadMatchingShadowState(context.ReleaseId);
            if (current is null || current.Protected) return Abstain("protected_or_unavailable_identity", 409);
            _releases.ValidateMatching(context.ReleaseId);
            var queries = SearchCandidateGenerator.Generate(new(current.RawTitle ?? "", context.UnifiedCategory));
            if (queries.Count == 0) return Abstain("no_query");
            // Explicitly bounded plan: every generated title, configured locale + English official search fields,
            // unfiltered years, and every page. Any uncovered part abstains, even after an excellent hit.
            var type = context.UnifiedCategory is UnifiedCategory.Serie or UnifiedCategory.Emission ? "tv" : "movie";
            var observed = new List<TmdbClient.ObservedSearchResult>();
            var used = 0;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            if (type == "movie" || _tmdb.MatchingConfigured)
            foreach (var title in queries.Select(q => q.Title).Distinct(StringComparer.Ordinal))
            foreach (var language in _tmdb.VideoMatchingLanguages)
            {
                var pages = 1;
                int? total = null;
                var ids = new HashSet<int>();
                for (var page = 1; page <= pages; page++)
                {
                    if (++used > 12) return Abstain("not_evaluable_search_budget");
                    var response = await RunTmdbAsync(inner => _tmdb.SearchVideoMatchingPageAsync(title, language, page, type, inner), timeout.Token).ConfigureAwait(false);
                    if (response is null) return Abstain("not_evaluable_provider_coverage");
                    if (total.HasValue && (response.TotalResults != total || response.TotalPages != pages))
                        return Abstain("not_evaluable_provider_changed_during_pagination");
                    total = response.TotalResults;
                    pages = response.TotalPages;
                    if (pages > 3) return Abstain("not_evaluable_page_budget");
                    foreach (var result in response.Results)
                    {
                        if (!ids.Add(result.TmdbId)) return Abstain("not_evaluable_duplicate_page_identity");
                        observed.Add(new(result, language));
                    }
                }
            }
            if (type == "tv" && !_tmdb.MatchingConfigured && !context.TvmazeEnabled) return Abstain("not_evaluable_no_tv_provider");
            var projected = TmdbShadowCandidateAdapter.Project(observed);
            if (projected.Length > new CandidateRankingPolicy().MaximumResults) return Abstain("not_evaluable_result_budget");
            if (type == "tv")
                projected = await TelevisionCandidates.EnrichAsync(queries, context.UnifiedCategory, current.RawTitle ?? "", projected,
                    async (provider, path, token) => {
                        if (++used > 30) throw new InvalidDataException("tv_http_budget");
                        return provider == "tmdb" ? await RunTmdbAsync(inner => _tmdb.GetTelevisionEvidenceAsync(path, inner), token).ConfigureAwait(false) :
                            await RunTvMazeAsync(inner => _tvmaze.GetMatchingEvidenceAsync(path, inner), token).ConfigureAwait(false);
                    }, context.TvmazeEnabled, timeout.Token).ConfigureAwait(false);
            var parsed = new TitleParser().Parse(current.RawTitle ?? "", context.UnifiedCategory);
            var ranked = SearchCandidateRanker.Rank(new(queries, context.UnifiedCategory, type == "movie" ? SearchMediaType.Movie : SearchMediaType.Series,
                Season: parsed.Season, Episode: parsed.Episode, AirDate: type == "tv" ? parsed.AirDate : null), projected);
            if (ranked.Decision != CandidateDecision.Accept || ranked.SelectedIdentity is not { } identity)
                return Abstain(ranked.Decision.ToString().ToLowerInvariant());
            if (_releases.ReadMatchingShadowState(context.ReleaseId) != current) return Abstain("identity_changed", 409);
            _releases.ValidateMatching(context.ReleaseId);
            if (identity.Provider == "tvmaze")
                return await PublishTvMazeV2Async(context, int.Parse(identity.Id, System.Globalization.CultureInfo.InvariantCulture), timeout.Token).ConfigureAwait(false);
            var selected = observed.Where(r => r.Result.TmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture) == identity.Id)
                .OrderByDescending(r => !string.IsNullOrWhiteSpace(r.Result.PosterPath)).First().Result;
            var confidence = (float)ranked.Candidates.First(r => r.Identity == identity).Confidence;
            _logger.LogInformation("Matching V2 video accepted releaseId={ReleaseId} identity=tmdb:{MediaType}:{TmdbId} confidence={Confidence} searchRequests={SearchRequests}",
                context.ReleaseId, type, identity.Id, confidence, used);
            // One identity only. Preparation/publication is isolated from the legacy decision path;
            // missing mandatory TMDB evidence abstains rather than choosing another work/provider.
            return await PublishVideoV2Async(context, selected, timeout.Token).ConfigureAwait(false);
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
