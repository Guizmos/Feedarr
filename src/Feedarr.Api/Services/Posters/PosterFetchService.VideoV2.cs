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
        => ResolveVideoType(snapshot, category) is not null;

    internal static string? ResolveVideoType(ReleaseForPoster snapshot, UnifiedCategory category)
    {
        if (category == UnifiedCategory.Film) return "movie";
        if (category is UnifiedCategory.Serie or UnifiedCategory.Emission) return "tv";
        if (category is not (UnifiedCategory.Animation or UnifiedCategory.Spectacle or UnifiedCategory.Autre) || string.IsNullOrWhiteSpace(snapshot.Title)) return null;
        var parents = (snapshot.CategoryIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var id) ? id : 0)
            .Append(snapshot.StdCategoryId ?? 0).Append(snapshot.CategoryId ?? 0).Append(snapshot.SpecCategoryId ?? 0)
            .Where(CategorySelection.IsStandardById).Select(CategorySelection.ToStandardParentId).Distinct().ToArray();
        // Mixed standard families and non-video families cannot be overridden by title tokens.
        if (parents.Length > 1 || parents.Any(parent => parent is not (2000 or 5000))) return null;
        var parser = new TitleParser();
        var episodic = parser.Parse(snapshot.Title, UnifiedCategory.Serie);
        var broadcast = parser.Parse(snapshot.Title, UnifiedCategory.Emission);
        var tv = snapshot.Season.HasValue || snapshot.Episode.HasValue || episodic.Season.HasValue ||
            episodic.Episode.HasValue || broadcast.AirDate is not null;
        if (parents.Contains(2000))
            return tv || TmdbIdentity.NormalizeType(snapshot.MediaType) == "tv" || TmdbIdentity.NormalizeType(snapshot.TmdbMediaType) == "tv" ? null : "movie";
        if (tv || parents.Contains(5000))
            return TmdbIdentity.NormalizeType(snapshot.TmdbMediaType) == "movie" ? null : "tv";
        // A derived movie/unknown MediaType and absence of episodes prove nothing.
        return null;
    }

    internal async Task<PosterFetchResult> FetchVideoV2Async(PosterFetchRoutingContext context, CancellationToken ct)
    {
        var stage = "collection";
        string? provider = "tmdb";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Enter(string next, string? source) { stage = next; provider = source; clock.Restart(); }
        void Record(string reason, string category, string exceptionType = "none")
        {
            _logger.LogWarning("Matching V2 diagnostic releaseId={ReleaseId} category={Category} stage={Stage} provider={Provider} elapsedMs={ElapsedMs} errorCategory={ErrorCategory} exceptionType={ExceptionType} decision={Decision}",
                context.ReleaseId, context.UnifiedCategory, stage, provider, clock.ElapsedMilliseconds, category, exceptionType, reason);
            try { PosterAudit.UpdateAttemptFailure(_releases, context.ReleaseId, null, null, null, null, $"v2:{stage}:{category}:{reason}"); }
            catch (ReleaseRepository.StaleMatchException) { throw; }
            catch { _logger.LogWarning("Matching V2 diagnostic persistence failed releaseId={ReleaseId}", context.ReleaseId); }
        }
        PosterFetchResult Abstain(string reason, int status = 422)
        {
            Record(reason, reason.StartsWith("not_evaluable", StringComparison.Ordinal) ? "coverage_insufficient" : "business_abstention");
            _logger.LogInformation("Matching V2 video abstained releaseId={ReleaseId} reason={Reason}", context.ReleaseId, reason);
            return new(false, status, new { error = "matching_v2_abstained", reason }, context.SourceId);
        }
        try
        {
            var current = _releases.ReadMatchingShadowState(context.ReleaseId);
            if (current is null || current.Protected) return Abstain("protected_or_unavailable_identity", 409);
            _releases.ValidateMatching(context.ReleaseId);
            var type = context.VideoType;
            if (type is not ("movie" or "tv")) return Abstain("video_type_unproven_or_conflicting");
            // Interpret TV tokens without changing the persisted category (Animation/Spectacle/Autre).
            var parsingCategory = type == "tv" && context.UnifiedCategory is not (UnifiedCategory.Serie or UnifiedCategory.Emission)
                ? new TitleParser().Parse(current.RawTitle ?? "", UnifiedCategory.Emission).AirDate is not null ? UnifiedCategory.Emission : UnifiedCategory.Serie
                : context.UnifiedCategory == UnifiedCategory.Autre ? UnifiedCategory.Film : context.UnifiedCategory;
            var queries = SearchCandidateGenerator.Generate(new(current.RawTitle ?? "", parsingCategory));
            if (queries.Count == 0) return Abstain("no_query");
            // Explicitly bounded plan: every generated title, configured locale + English official search fields,
            // unfiltered years, and every page. Any uncovered part abstains, even after an excellent hit.
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
            Enter("enrichment", null);
            if (type == "tv")
                projected = await TelevisionCandidates.EnrichAsync(queries, parsingCategory, current.RawTitle ?? "", projected,
                    async (evidenceProvider, path, token) => {
                        provider = evidenceProvider;
                        if (++used > 30) throw new InvalidDataException("tv_http_budget");
                        return evidenceProvider == "tmdb" ? await RunTmdbAsync(inner => _tmdb.GetTelevisionEvidenceAsync(path, inner), token).ConfigureAwait(false) :
                            await RunTvMazeAsync(inner => _tvmaze.GetMatchingEvidenceAsync(path, inner), token).ConfigureAwait(false);
                    }, context.TvmazeEnabled, timeout.Token).ConfigureAwait(false);
            var parsed = new TitleParser().Parse(current.RawTitle ?? "", parsingCategory);
            Enter("ranking", null);
            var ranked = SearchCandidateRanker.Rank(new(queries, parsingCategory, type == "movie" ? SearchMediaType.Movie : SearchMediaType.Series,
                Season: parsed.Season, Episode: parsed.Episode, AirDate: type == "tv" ? parsed.AirDate : null), projected);
            if (ranked.Decision != CandidateDecision.Accept || ranked.SelectedIdentity is not { } identity)
                return Abstain(ranked.Decision.ToString().ToLowerInvariant());
            if (_releases.ReadMatchingShadowState(context.ReleaseId) != current) return Abstain("identity_changed", 409);
            _releases.ValidateMatching(context.ReleaseId);
            Enter("preparation", identity.Provider);
            if (identity.Provider == "tvmaze")
                return await PublishTvMazeV2Async(context, int.Parse(identity.Id, System.Globalization.CultureInfo.InvariantCulture), timeout.Token, diagnosticStage: Enter, diagnosticFailure: error => Record("derived_pending", error is OperationCanceledException ? "cancellation" : "materialization_error", error.GetType().Name)).ConfigureAwait(false);
            var selected = observed.Where(r => r.Result.TmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture) == identity.Id)
                .OrderByDescending(r => !string.IsNullOrWhiteSpace(r.Result.PosterPath)).First().Result;
            var confidence = (float)ranked.Candidates.First(r => r.Identity == identity).Confidence;
            _logger.LogInformation("Matching V2 video accepted releaseId={ReleaseId} identity=tmdb:{MediaType}:{TmdbId} confidence={Confidence} searchRequests={SearchRequests}",
                context.ReleaseId, type, identity.Id, confidence, used);
            // One identity only. Preparation/publication is isolated from the legacy decision path;
            // missing mandatory TMDB evidence abstains rather than choosing another work/provider.
            return await PublishVideoV2Async(context, selected, timeout.Token, diagnosticStage: Enter, diagnosticFailure: error => Record("derived_pending", error is OperationCanceledException ? "cancellation" : "materialization_error", error.GetType().Name)).ConfigureAwait(false);
        }
        catch (ReleaseRepository.StaleMatchException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Record("cancelled", "cancellation", nameof(OperationCanceledException));
            throw;
        }
        catch (Exception error)
        {
            var failure = VideoMatchingFailure.Classify(error, stage);
            Record(failure.Category, failure.Category, error.GetType().Name);
            return new(false, failure.Status, new { error = "matching_v2_abstained", reason = "not_evaluable_provider_or_publication_error" }, context.SourceId);
        }
    }
}
