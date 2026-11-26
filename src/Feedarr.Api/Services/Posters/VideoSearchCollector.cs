using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Posters;

internal sealed record VideoSearchCollection(IReadOnlyList<TmdbClient.ObservedSearchResult> Observed,
    string? FailureReason, VideoResultBudgetCounters Counters, int RequiredPages, string SearchMode = "general")
{
    internal bool Complete => FailureReason is null;
    internal int PageLimit { get; init; } = VideoSearchCollector.MaximumPages;
}

internal static class VideoSearchCollector
{
    internal const int MaximumPages = 12;
    internal const int ExtendedPages = 18;

    private sealed class Plan(string title, string language, int? year = null)
    {
        internal readonly string Title = title;
        internal readonly string Language = language;
        internal readonly int? Year = year;
        internal readonly HashSet<int> Ids = new();
        internal int? TotalResults;
        internal int TotalPages = 1;
    }

    internal static Task<VideoSearchCollection> CollectAsync(IEnumerable<string> titles,
        IEnumerable<string> languages,
        Func<string, string, int, CancellationToken, Task<TmdbClient.VideoMatchingPage?>> getPage,
        CancellationToken ct, int pageBudget = MaximumPages,
        Func<IReadOnlyList<TmdbClient.ObservedSearchResult>, bool>? extendWhen = null, int maximumPageBudget = ExtendedPages)
    {
        var locales = languages.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var plans = titles.Distinct(StringComparer.Ordinal).SelectMany(title =>
            locales.Select(language => new Plan(title, language))).ToArray();
        return CollectPlansAsync(plans, (title, language, page, _, token) => getPage(title, language, page, token), ct, pageBudget, extendWhen, maximumPageBudget);
    }

    internal static Task<VideoSearchCollection> CollectYearsAsync(IEnumerable<string> titles,
        IEnumerable<string> languages, IEnumerable<int> years,
        Func<string, string, int, int?, CancellationToken, Task<TmdbClient.VideoMatchingPage?>> getPage,
        CancellationToken ct, Func<IReadOnlyList<TmdbClient.ObservedSearchResult>, bool>? extendWhen = null)
    {
        var locales = languages.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var window = years.Distinct().ToArray();
        var plans = titles.Distinct(StringComparer.Ordinal).SelectMany(title => window.SelectMany(year =>
            locales.Select(language => new Plan(title, language, year)))).ToArray();
        return CollectPlansAsync(plans, getPage, ct, MaximumPages, extendWhen, ExtendedPages);
    }

    private static async Task<VideoSearchCollection> CollectPlansAsync(Plan[] plans,
        Func<string, string, int, int?, CancellationToken, Task<TmdbClient.VideoMatchingPage?>> getPage,
        CancellationToken ct, int pageBudget, Func<IReadOnlyList<TmdbClient.ObservedSearchResult>, bool>? extendWhen, int maximumPageBudget)
    {
        if (maximumPageBudget is < 0 or > ExtendedPages || pageBudget < 0 || pageBudget > maximumPageBudget) throw new ArgumentOutOfRangeException(nameof(pageBudget));
        var observed = new List<TmdbClient.ObservedSearchResult>();
        var requests = 0;
        var received = 0;
        var started = 0;
        var required = plans.Length; // Reserve at least the first page of every title/language plan.
        VideoResultBudgetCounters counters = new(0, 0, 0, 0);
        VideoSearchCollection Failure(string reason) => new(Array.Empty<TmdbClient.ObservedSearchResult>(), reason, counters, required) { PageLimit = pageBudget };

        async Task<string?> ReadAsync(Plan plan, int page)
        {
            ct.ThrowIfCancellationRequested();
            if (requests >= pageBudget) return "not_evaluable_search_budget";
            if (page == 1) started++;
            requests++;
            counters = new(started, requests, received, observed.Count, page, plan.TotalPages, plan.TotalResults);
            // The TMDB client also validates the response's page number and individual records.
            var response = await getPage(plan.Title, plan.Language, page, plan.Year, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (response is null) return "not_evaluable_provider_coverage";
            received++;
            counters = new(started, requests, received, observed.Count, page, response.TotalPages, response.TotalResults);
            if (plan.TotalResults.HasValue &&
                (response.TotalResults != plan.TotalResults || response.TotalPages != plan.TotalPages))
                return "not_evaluable_provider_changed_during_pagination";
            if (response.TotalResults < 0 || response.TotalPages is < 0 or > 500 ||
                (response.TotalResults == 0
                    ? page != 1 || response.TotalPages is not (0 or 1) || response.Results.Count != 0
                    : response.TotalPages != (int)Math.Ceiling(response.TotalResults / 20d) ||
                      page > response.TotalPages || response.Results.Count != Math.Min(20, response.TotalResults - (page - 1) * 20)))
                return "not_evaluable_provider_coverage";
            if (page == 1)
            {
                plan.TotalResults = response.TotalResults;
                plan.TotalPages = response.TotalPages;
                required += Math.Max(1, plan.TotalPages) - 1;
            }
            foreach (var row in response.Results)
            {
                if (!plan.Ids.Add(row.TmdbId)) return "not_evaluable_duplicate_page_identity";
                observed.Add(new(row, plan.Language));
            }
            counters = counters with { ObservedResults = observed.Count };
            if (required > pageBudget && required <= maximumPageBudget && extendWhen?.Invoke(observed) == true)
                pageBudget = maximumPageBudget;
            return required > pageBudget ? "not_evaluable_search_budget" : null;
        }

        ct.ThrowIfCancellationRequested();
        if (required > pageBudget) return Failure("not_evaluable_search_budget");
        // Discover all pagination plans before requesting their remaining pages.
        foreach (var plan in plans)
            if (await ReadAsync(plan, 1).ConfigureAwait(false) is { } failure) return Failure(failure);
        foreach (var plan in plans)
        {
            for (var page = 2; page <= plan.TotalPages; page++)
                if (await ReadAsync(plan, page).ConfigureAwait(false) is { } failure) return Failure(failure);
            if (plan.Ids.Count != plan.TotalResults) return Failure("not_evaluable_provider_coverage");
        }
        ct.ThrowIfCancellationRequested();
        return new(observed, null, counters, required) { PageLimit = pageBudget };
    }
}
