using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Matching;

internal static class TmdbShadowCandidateAdapter
{
    internal static ProviderSearchCandidate[] Project(IReadOnlyList<TmdbClient.ObservedSearchResult> observed)
    {
        static SearchIdentity Identity(TmdbClient.SearchResult r) => new("tmdb",
                TmdbIdentity.NormalizeType(r.MediaType) == "movie" ? SearchMediaType.Movie : SearchMediaType.Series,
                r.TmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return observed.GroupBy(r => Identity(r.Result)).SelectMany(group =>
            {
                var identity = group.Key;
                var titles = group.SelectMany(r => new[]
                {
                    new OfficialProviderTitle(r.Result.Title, identity,
                        r.ResponseLanguage is null ? ProviderTitleKind.ResponseTitle : ProviderTitleKind.Localized,
                        ProviderTitleSource.SearchTitle, r.ResponseLanguage),
                    new OfficialProviderTitle(r.Result.OriginalTitle ?? "", identity, ProviderTitleKind.Original,
                        ProviderTitleSource.SearchOriginalTitle, r.Result.OriginalLanguage)
                }).Where(t => !string.IsNullOrWhiteSpace(t.Title)).Distinct().ToArray();
                // Preserve conflicting years as separate records: the ranker must not hide them.
                return group.GroupBy(r => r.Result.Year).Select(rows => new ProviderSearchCandidate(identity,
                    rows.First().Result.Title, rows.Key, OfficialTitles: titles));
            }).ToArray();
    }
}
