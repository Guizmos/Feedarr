using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Posters;

/// <summary>Reliable movie years narrow collection, never change ranking or authorize partial coverage.</summary>
internal static class MovieYearSearchCollector
{
    internal static async Task<VideoSearchCollection> CollectAsync(CandidateRankingContext context,
        IEnumerable<string> languages,
        Func<string, string, int, int?, CancellationToken, Task<TmdbClient.VideoMatchingPage?>> getPage,
        CancellationToken ct, Action<VideoCollectionDiagnostic>? diagnostic = null)
    {
        var titles = context.Queries.Select(q => q.Title).ToArray();
        var locales = languages.ToArray();
        var reliableMovie = TryGetReliableYear(context, out var year);
        bool Promising(IReadOnlyList<TmdbClient.ObservedSearchResult> rows) =>
            !context.IsLocked && !context.HasManualCorrection && context.EstablishedIdentity is null &&
            (reliableMovie || context.ExpectedMediaType == SearchMediaType.Series &&
                (context.Season.HasValue || context.Episode.HasValue || context.AirDate is not null)) &&
            rows.Any(r => TmdbIdentity.NormalizeType(r.Result.MediaType) == (context.ExpectedMediaType == SearchMediaType.Movie ? "movie" : "tv") &&
                (!reliableMovie || r.Result.Year == year) && context.Queries.Any(q =>
                    TitleNormalizer.NormalizeTitleStrict(q.Title) == TitleNormalizer.NormalizeTitleStrict(r.Result.Title) ||
                    TitleNormalizer.NormalizeTitleStrict(q.Title) == TitleNormalizer.NormalizeTitleStrict(r.Result.OriginalTitle)));
        async Task<VideoSearchCollection> General(int remaining, int maximum) => await VideoSearchCollector.CollectAsync(titles, locales,
            (title, language, page, token) => getPage(title, language, page, null, token), ct, remaining, Promising, maximum).ConfigureAwait(false);
        if (!reliableMovie)
        {
            var general = await General(VideoSearchCollector.MaximumPages, VideoSearchCollector.ExtendedPages).ConfigureAwait(false);
            diagnostic?.Invoke(VideoCollectionDiagnostic.From(general));
            return general;
        }

        var tolerance = new CandidateRankingPolicy().ParsedYearTolerance;
        var years = new[] { year }.Concat(Enumerable.Range(year - tolerance, tolerance * 2 + 1).Where(y => y != year));
        var targeted = (await VideoSearchCollector.CollectYearsAsync(titles, locales, years, getPage, ct, Promising).ConfigureAwait(false))
            with { SearchMode = "targeted_year" };
        diagnostic?.Invoke(VideoCollectionDiagnostic.From(targeted));
        if (!targeted.Complete) return targeted;

        // Projection preserves original/localized titles, repeated identities and conflicting records.
        // An ambiguity or a projection overflow must not be hidden by another search.
        var candidates = TmdbShadowCandidateAdapter.Project(targeted.Observed, context);
        var targetedRanking = SearchCandidateRanker.Rank(context, candidates);
        if (candidates.Length > new CandidateRankingPolicy().MaximumResults ||
            targetedRanking.Decision != CandidateDecision.Reject || MovieExactYearPriority.HasResolvableNumericCandidate(targetedRanking))
            return targeted;

        var fallback = await General(Math.Max(0, targeted.PageLimit - targeted.Counters.SearchRequests), Math.Max(0, VideoSearchCollector.ExtendedPages - targeted.Counters.SearchRequests)).ConfigureAwait(false);
        // Only a complete fallback may expose candidates. Carry targeted records too, so no
        // disagreement between filtered/general responses can disappear during ranking.
        var combined = fallback with
        {
            SearchMode = "general_fallback",
            Observed = fallback.Complete
                ? targeted.Observed.Concat(fallback.Observed).Distinct().ToArray()
                : Array.Empty<TmdbClient.ObservedSearchResult>(),
            Counters = fallback.Counters with {
                SearchQueries = targeted.Counters.SearchQueries + fallback.Counters.SearchQueries,
                SearchRequests = targeted.Counters.SearchRequests + fallback.Counters.SearchRequests,
                PagesReceived = targeted.Counters.PagesReceived + fallback.Counters.PagesReceived,
                ObservedResults = targeted.Counters.ObservedResults + fallback.Counters.ObservedResults
            },
            RequiredPages = targeted.Counters.SearchRequests + fallback.RequiredPages,
            PageLimit = targeted.Counters.SearchRequests + fallback.PageLimit
        };
        diagnostic?.Invoke(VideoCollectionDiagnostic.From(combined));
        return combined;
    }

    private static bool TryGetReliableYear(CandidateRankingContext context, out int year)
    {
        year = 0;
        if (context.ExpectedMediaType != SearchMediaType.Movie ||
            context.IsLocked || context.HasManualCorrection || context.EstablishedIdentity is not null ||
            context.TrustedIdentityHint is not null || context.ConfirmedYear.HasValue ||
            // RSS identity corroboration can make a missing-year candidate competitive.
            // Preserve general search for compatible evidence; TVDB cannot identify a movie.
            context.RssEvidence is { TmdbId: not null } or { ImdbId: not null } or { ConflictingClaims: true } ||
            context.Season.HasValue || context.Episode.HasValue || context.AirDate is not null ||
            context.Queries.Count == 0 || context.Queries.Count > SearchCandidateGenerator.MaxCandidates ||
            context.Queries.Any(q => q.YearEvidence != CandidateYearEvidence.Parsed || q.Year is not (>= 1801 and <= 2099)))
            return false;
        var years = context.Queries.Select(q => q.Year!.Value).Distinct().ToArray();
        if (years.Length != 1) return false;
        year = years[0];
        return true;
    }
}

internal sealed record VideoCollectionDiagnostic(string SearchMode, string Outcome, int SearchRequests, int RequiredPages, int PageLimit = VideoSearchCollector.MaximumPages)
{
    internal static VideoCollectionDiagnostic From(VideoSearchCollection collection) => new(collection.SearchMode,
        collection.FailureReason ?? "complete", collection.Counters.SearchRequests, collection.RequiredPages, collection.PageLimit);

    internal void Log(ILogger logger, long releaseId, UnifiedCategory category)
    {
        MatchingAttemptDiagnostics.Current?.Collection(SearchMode, Outcome, SearchRequests, RequiredPages);
        // Controlled codes/counters only. No query, title, URL, response body or credential.
        logger.LogInformation("Matching V2 collection plan releaseId={ReleaseId} category={Category} searchMode={SearchMode} outcome={Outcome} searchRequests={SearchRequests} requiredPages={RequiredPages} pageBudget={PageBudget}",
            releaseId, category, SearchMode, Outcome, SearchRequests, RequiredPages, PageLimit);
    }
}
