using Feedarr.Api.Data.Repositories;

namespace Feedarr.Api.Services.Metadata;

public sealed record RequestTmdbBackfillResult(
    int MissingCandidates,
    int ReusedExistingTmdb,
    int ResolvedFromTvdbMap,
    int ResolvedFromTitleSearch,
    int Unresolved);

public sealed class RequestTmdbBackfillService
{
    private readonly ReleaseRepository _releases;
    private readonly RequestTmdbResolverService _resolver;

    public RequestTmdbBackfillService(
        ReleaseRepository releases,
        RequestTmdbResolverService resolver)
    {
        _releases = releases;
        _resolver = resolver;
    }

    public async Task<RequestTmdbBackfillResult> BackfillSeriesRequestTmdbAsync(int limit, CancellationToken ct)
    {
        var lim = Math.Clamp(limit <= 0 ? 200 : limit, 1, 5000);
        var rows = _releases.GetSeriesMissingRequestTmdb(lim);
        var scope = new HashSet<string>(StringComparer.Ordinal);

        var reusedExistingTmdb = 0;
        var resolvedFromTvdbMap = 0;
        var resolvedFromTitleSearch = 0;
        var unresolved = 0;

        foreach (var row in rows)
        {
            var key = BuildScopeKey(row);
            if (!scope.Add(key))
                continue;

            var result = await _resolver.ResolveAsync(
                new RequestTmdbResolveInput(
                    row.ReleaseId,
                    row.MediaType,
                    row.TmdbId,
                    row.TvdbId,
                    row.TitleClean,
                    row.Year),
                ct);

            switch (result.Status)
            {
                case "from_existing_tmdb":
                    reusedExistingTmdb++;
                    break;
                case "resolved_tvdb_map":
                    resolvedFromTvdbMap++;
                    break;
                case "resolved_title_search":
                    resolvedFromTitleSearch++;
                    break;
                default:
                    unresolved++;
                    break;
            }
        }

        return new RequestTmdbBackfillResult(
            rows.Count,
            reusedExistingTmdb,
            resolvedFromTvdbMap,
            resolvedFromTitleSearch,
            unresolved);
    }

    private static string BuildScopeKey(ReleaseRepository.RequestTmdbSeedRow row)
    {
        if (row.EntityId.HasValue && row.EntityId.Value > 0)
            return $"e:{row.EntityId.Value}";
        return $"r:{row.ReleaseId}";
    }
}
