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
        => ResolveVideoType(snapshot, category, out _);

    internal static string? ResolveVideoType(ReleaseForPoster snapshot, UnifiedCategory category, out string refusalReason)
    {
        refusalReason = "video_type_unproven";
        if (category is UnifiedCategory.Film or UnifiedCategory.Animation && !string.IsNullOrWhiteSpace(snapshot.Title))
        {
            var strong = new TitleParser().Parse(snapshot.Title, UnifiedCategory.Serie);
            var episodicParents = (snapshot.CategoryIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.TryParse(value, out var id) ? id : 0)
                .Append(snapshot.StdCategoryId ?? 0).Append(snapshot.CategoryId ?? 0).Append(snapshot.SpecCategoryId ?? 0)
                .Where(CategorySelection.IsStandardById).Select(CategorySelection.ToStandardParentId).Distinct().ToArray();
            var validFamily = episodicParents.Length <= 1 && episodicParents.All(p => p is 2000 or 5000);
            var proven = strong.Season.HasValue && (strong.Episode.HasValue || category == UnifiedCategory.Animation &&
                (strong.Source is not null || strong.Resolution is not null || strong.Codec is not null));
            if (proven && !validFamily)
            { refusalReason = "episodic_evidence_conflicts_with_standard_families"; return null; }
            if (proven && validFamily && TmdbIdentity.NormalizeType(snapshot.TmdbMediaType) != "movie") return "tv";
        }
        if (category == UnifiedCategory.Film) return "movie";
        if (category is UnifiedCategory.Serie or UnifiedCategory.Emission) return "tv";
        if (category is not (UnifiedCategory.Animation or UnifiedCategory.Spectacle or UnifiedCategory.Autre) || string.IsNullOrWhiteSpace(snapshot.Title))
        { refusalReason = "unsupported_category_or_missing_title"; return null; }
        var parents = (snapshot.CategoryIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var id) ? id : 0)
            .Append(snapshot.StdCategoryId ?? 0).Append(snapshot.CategoryId ?? 0).Append(snapshot.SpecCategoryId ?? 0)
            .Where(CategorySelection.IsStandardById).Select(CategorySelection.ToStandardParentId).Distinct().ToArray();
        // Mixed standard families and non-video families cannot be overridden by title tokens.
        if (parents.Length > 1) { refusalReason = "conflicting_standard_families"; return null; }
        if (parents.Any(parent => parent is not (2000 or 5000))) { refusalReason = "non_video_standard_family"; return null; }
        var parser = new TitleParser();
        var episodic = parser.Parse(snapshot.Title, UnifiedCategory.Serie);
        var broadcast = parser.Parse(snapshot.Title, UnifiedCategory.Emission);
        var tv = snapshot.Season.HasValue || snapshot.Episode.HasValue || episodic.Season.HasValue ||
            episodic.Episode.HasValue || broadcast.AirDate is not null;
        // Animation is often filed under Movies/Anime by indexers. A complete
        // season+episode token proves episodic scope, but never overrides a typed association.
        if (category == UnifiedCategory.Animation && episodic.Season.HasValue && episodic.Episode.HasValue &&
            TmdbIdentity.NormalizeType(snapshot.TmdbMediaType) != "movie") return "tv";
        if (parents.Contains(2000))
        {
            refusalReason = "movie_family_conflicts_with_tv_evidence";
            return tv || TmdbIdentity.NormalizeType(snapshot.MediaType) == "tv" || TmdbIdentity.NormalizeType(snapshot.TmdbMediaType) == "tv" ? null : "movie";
        }
        if (tv || parents.Contains(5000))
        {
            refusalReason = "tv_evidence_conflicts_with_movie_identity";
            return TmdbIdentity.NormalizeType(snapshot.TmdbMediaType) == "movie" ? null : "tv";
        }
        // Direct RSS has configured functional categories, not Torznab standard families.
        // Reparse persisted titles so existing releases benefit without rewriting categories.
        // A default movie type alone is insufficient: require fresh parsed title/year,
        // no TV evidence, and no conflicting standard or associated type.
        if (parents.Length == 0 && snapshot.IsDirectRss &&
            category is UnifiedCategory.Animation or UnifiedCategory.Spectacle &&
            TmdbIdentity.NormalizeType(snapshot.MediaType) == "movie" &&
            TmdbIdentity.NormalizeType(snapshot.TmdbMediaType) != "tv" &&
            !string.IsNullOrWhiteSpace(snapshot.Title))
        {
            var fresh = parser.Parse(snapshot.Title, category);
            var queries = SearchCandidateGenerator.Generate(new(snapshot.Title, category));
            if (queries.Count > 0 && queries.All(q => q.YearEvidence == CandidateYearEvidence.Parsed &&
                q.Year is >= 1801 and <= 2099 && !string.IsNullOrWhiteSpace(q.Title) &&
                (!snapshot.Year.HasValue || snapshot.Year == q.Year &&
                    (TitleNormalizer.NormalizeTitleStrict(snapshot.TitleClean) == TitleNormalizer.NormalizeTitleStrict(q.Title) ||
                     fresh.Edition is not null && TitleNormalizer.NormalizeTitleStrict(snapshot.TitleClean) ==
                        TitleNormalizer.NormalizeTitleStrict(q.Title + " " + fresh.Edition))))) return "movie";
        }
        if (snapshot.IsDirectRss && category is UnifiedCategory.Animation or UnifiedCategory.Spectacle)
            refusalReason = TmdbIdentity.NormalizeType(snapshot.MediaType) != "movie" ? "direct_rss_movie_type_unconfirmed" :
                snapshot.Year is not (>= 1801 and <= 2099) ? "direct_rss_parsed_year_unavailable" :
                string.IsNullOrWhiteSpace(snapshot.TitleClean) ? "direct_rss_clean_title_unavailable" : "direct_rss_movie_parsing_inconsistent";
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
            MatchingAttemptDiagnostics.Current?.SetOutcome(stage, reason);
            _logger.LogWarning("Matching V2 diagnostic releaseId={ReleaseId} category={Category} stage={Stage} provider={Provider} elapsedMs={ElapsedMs} errorCategory={ErrorCategory} exceptionType={ExceptionType} decision={Decision}",
                context.ReleaseId, context.UnifiedCategory, stage, provider, clock.ElapsedMilliseconds, category, exceptionType, reason);
            try { PosterAudit.UpdateAttemptFailure(_releases, context.ReleaseId, null, null, null, null, $"v2:{stage}:{category}:{reason}"); }
            catch (ReleaseRepository.StaleMatchException) { throw; }
            catch { _logger.LogWarning("Matching V2 diagnostic persistence failed releaseId={ReleaseId}", context.ReleaseId); }
        }
        PosterFetchResult Abstain(string reason, int status = 422, string? diagnosticReason = null)
        {
            Record(diagnosticReason ?? reason, reason.StartsWith("not_evaluable", StringComparison.Ordinal) ? "coverage_insufficient" : "business_abstention");
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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var rssEvidence = RssMatchingEvidence.Read(current.RssEvidenceJson);
            if (await TryFetchRssIdentityAsync(context, current, rssEvidence, timeout.Token, Enter,
                error => Record("derived_pending", error is OperationCanceledException ? "cancellation" : "materialization_error", error.GetType().Name)).ConfigureAwait(false) is { } direct)
                return direct;
            var queries = SearchCandidateGenerator.Generate(new(current.RawTitle ?? "", parsingCategory));
            if (queries.Count == 0) return Abstain("no_query");
            var parsed = type == "tv" ? TelevisionSearchInput.Parse(current.RawTitle ?? "") : new TitleParser().Parse(current.RawTitle ?? "", parsingCategory);
            if (type == "tv") MatchingAttemptDiagnostics.Current?.Collection("tv_input",
                $"season={parsed.Season};episode={parsed.Episode};airDate={parsed.AirDate};broadcastYear={parsed.BroadcastYear};yearRoles={string.Join('+', queries.Select(q => q.YearEvidence).Distinct())};queries={queries.Count}");
            var rankingContext = new CandidateRankingContext(queries, parsingCategory,
                type == "movie" ? SearchMediaType.Movie : SearchMediaType.Series,
                Season: parsed.Season, Episode: parsed.Episode, AirDate: type == "tv" ? parsed.AirDate : null, RssEvidence: rssEvidence);
            // Reliable films cover the parsed year and every admissible neighboring year first.
            // Targeted and fallback collection share one budget, extended only on a promising typed plan.
            IReadOnlyList<TmdbClient.ObservedSearchResult> observed = Array.Empty<TmdbClient.ObservedSearchResult>();
            var used = 0;
            var searchQueries = 0;
            var searchRequests = 0;
            var pagesReceived = 0;
            VideoSearchCollection? completeCollection = null;
            if (type == "movie" || _tmdb.MatchingConfigured)
            {
                var collection = await MovieYearSearchCollector.CollectAsync(rankingContext,
                    _tmdb.VideoMatchingLanguages,
                    (title, language, page, year, token) => RunTmdbAsync(
                        inner => _tmdb.SearchVideoMatchingPageAsync(title, language, page, type, inner, year), token),
                    timeout.Token, diagnostic => diagnostic.Log(_logger, context.ReleaseId, context.UnifiedCategory)).ConfigureAwait(false);
                used = searchRequests = collection.Counters.SearchRequests;
                searchQueries = collection.Counters.SearchQueries;
                pagesReceived = collection.Counters.PagesReceived;
                if (!collection.Complete)
                {
                    if (collection.FailureReason == "not_evaluable_search_budget" &&
                        VideoResultBudgetDiagnostic.CheckSearchPages(collection.RequiredPages,
                            collection.PageLimit, collection.Counters) is { } pageBudget)
                        pageBudget.Log(_logger, context.ReleaseId, context.UnifiedCategory);
                    return Abstain(collection.FailureReason!);
                }
                observed = collection.Observed;
                completeCollection = collection;
            }
            if (type == "tv" && !_tmdb.MatchingConfigured && !context.TvmazeEnabled) return Abstain("not_evaluable_no_tv_provider");
            var projected = TmdbShadowCandidateAdapter.Project(observed,
                type == "tv" ? rankingContext : new(queries, parsingCategory, SearchMediaType.Movie));
            var projectionLimit = type == "tv" ? TelevisionCandidates.ProjectionLimit(rankingContext, projected) : new CandidateRankingPolicy().MaximumResults;
            if (type == "tv") MatchingAttemptDiagnostics.Current?.Collection("tv_projection",
                $"observed={observed.Count};projected={projected.Length};limit={projectionLimit}");
            if (VideoResultBudgetDiagnostic.CheckProjection(projected.Length, projectionLimit,
                new(searchQueries, searchRequests, pagesReceived, observed.Count)) is { } projectionBudget)
            {
                projectionBudget.Log(_logger, context.ReleaseId, context.UnifiedCategory);
                return Abstain(VideoResultBudgetDiagnostic.AbstentionReason, diagnosticReason: projectionBudget.Reason);
            }
            Enter("enrichment", null);
            var logicalLimit = projectionLimit == TelevisionCandidates.ExtendedCandidates ? TelevisionCandidates.LogicalRequestLimit : 30;
            if (type == "tv")
                projected = await TelevisionCandidates.EnrichAsync(queries, parsingCategory, current.RawTitle ?? "", projected,
                    async (evidenceProvider, path, token) => {
                        provider = evidenceProvider;
                        if (used >= logicalLimit)
                        {
                            MatchingAttemptDiagnostics.Current?.Collection("tv_cost", $"logicalRequests={used};limit={logicalLimit};transports={MatchingAttemptDiagnostics.Current?.TransportRequests};outcome=budget");
                            throw new InvalidDataException("tv_http_budget");
                        }
                        used++;
                        return evidenceProvider == "tmdb" ? await RunTmdbAsync(inner => _tmdb.GetTelevisionEvidenceAsync(path, inner), token).ConfigureAwait(false) :
                            await RunTvMazeAsync(inner => _tvmaze.GetMatchingEvidenceAsync(path, inner), token).ConfigureAwait(false);
                    }, context.TvmazeEnabled, timeout.Token,
                    diagnostic => MatchingAttemptDiagnostics.Current?.Collection("tv_evidence", diagnostic), rankingContext, limit => logicalLimit = limit).ConfigureAwait(false);
            MovieExactYearPriority? yearPriority = null;
            if (type == "movie")
            {
                provider = "tmdb";
                yearPriority = new(rankingContext, current.RawTitle ?? "", completeCollection, projected,
                    (id, token) => RunTmdbAsync(inner => _tmdb.GetMoviePriorityEvidenceAsync(id, inner), token),
                    (id, token) => RunTmdbAsync(inner => _tmdb.GetMovieMatchingTitlesAsync(id, inner), token));
                projected = await MovieTitleCandidates.EnrichAsync(new(queries, parsingCategory, SearchMediaType.Movie, RssEvidence: rssEvidence), projected,
                    yearPriority.LoadTitlesAsync, timeout.Token).ConfigureAwait(false);
            }
            Enter("ranking", null);
            if (type == "tv")
            {
                projectionLimit = TelevisionCandidates.ProjectionLimit(rankingContext, projected);
                MatchingAttemptDiagnostics.Current?.Collection("tv_cost", $"logicalRequests={used};limit={logicalLimit};transports={MatchingAttemptDiagnostics.Current?.TransportRequests}");
            }
            var ranked = SearchCandidateRanker.Rank(rankingContext, projected, new CandidateRankingPolicy(MaximumResults: projectionLimit, ContextualVideoDecision: true));
            if (yearPriority is not null)
            {
                ranked = await yearPriority.ApplyAsync(projected, ranked, timeout.Token).ConfigureAwait(false);
                used = yearPriority.Requests;
                _logger.LogInformation("Matching V2 exact year priority releaseId={ReleaseId} outcome={PriorityOutcome} decisionReason={PriorityReason} decisionRequests={DecisionRequests} limit={DecisionRequestLimit} verifiedYear={VerifiedYear} verifiedContenders={VerifiedContenders} conflictEvidence={ConflictEvidence} warnings={Warnings}",
                    context.ReleaseId, yearPriority.Outcome, ranked.Reasons.Contains(MovieExactYearPriority.DecisionReason) ? MovieExactYearPriority.DecisionReason : "legacy_decision_preserved", used, yearPriority.DecisionLimit, yearPriority.VerifiedYear, yearPriority.VerifiedContenders, yearPriority.ConflictEvidence, string.Join('+', yearPriority.Warnings));
            }
            MatchingAttemptDiagnostics.LogRanking(_logger, context.ReleaseId, ranked);
            if (ranked.Decision != CandidateDecision.Accept || ranked.SelectedIdentity is not { } identity)
                return Abstain(ranked.Decision.ToString().ToLowerInvariant(), diagnosticReason: ranked.Decision == CandidateDecision.Reject ? ranked.Candidates.Count == 0 ? "no_candidates" : "candidates_rejected" : "ambiguous");
            if (_releases.ReadMatchingShadowState(context.ReleaseId) != current) return Abstain("identity_changed", 409);
            _releases.ValidateMatching(context.ReleaseId);
            Enter("preparation", identity.Provider);
            if (identity.Provider == "tvmaze")
                return await PublishTvMazeV2Async(context, int.Parse(identity.Id, System.Globalization.CultureInfo.InvariantCulture), timeout.Token, diagnosticStage: Enter, diagnosticFailure: error => Record("derived_pending", error is OperationCanceledException ? "cancellation" : "materialization_error", error.GetType().Name)).ConfigureAwait(false);
            var selected = observed.Where(r => r.Result.TmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture) == identity.Id)
                .OrderByDescending(r => !string.IsNullOrWhiteSpace(r.Result.PosterPath)).First().Result;
            var confidence = (float)ranked.Candidates.First(r => r.Identity == identity).Confidence;
            _logger.LogInformation("Matching V2 video accepted releaseId={ReleaseId} identity=tmdb:{MediaType}:{TmdbId} confidence={Confidence} searchRequests={SearchRequests} decisionRequests={DecisionRequests}",
                context.ReleaseId, type, identity.Id, confidence, searchRequests, used);
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
            Record(failure.Invariant ?? failure.Category, failure.Category, error.GetType().Name);
            return new(false, failure.Status, new { error = "matching_v2_abstained", reason = "not_evaluable_provider_or_publication_error" }, context.SourceId);
        }
    }
}
