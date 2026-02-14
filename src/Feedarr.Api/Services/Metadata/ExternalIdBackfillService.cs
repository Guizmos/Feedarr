using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Metadata;

public sealed record ExternalIdsBackfillResult(
    int MissingTmdbCandidates,
    int ResolvedTmdb,
    int MissingTvdbCandidates,
    int ResolvedTvdb,
    int PropagatedToEntities);

public sealed class ExternalIdBackfillService
{
    private readonly ReleaseRepository _releases;
    private readonly TmdbClient _tmdb;
    private readonly ILogger<ExternalIdBackfillService> _logger;

    public ExternalIdBackfillService(
        ReleaseRepository releases,
        TmdbClient tmdb,
        ILogger<ExternalIdBackfillService> logger)
    {
        _releases = releases;
        _tmdb = tmdb;
        _logger = logger;
    }

    public async Task<ExternalIdsBackfillResult> BackfillSeriesExternalIdsAsync(int limit, CancellationToken ct)
    {
        var lim = Math.Clamp(limit <= 0 ? 200 : limit, 1, 5000);
        var missingTmdbRows = _releases.GetSeriesMissingTmdbWithTvdb(lim);
        var missingTvdbRows = _releases.GetSeriesMissingTvdbWithTmdb(lim);

        var tmdbByTvdb = new Dictionary<int, int?>();
        var tvdbByTmdb = new Dictionary<int, int?>();
        var tmdbScope = new HashSet<string>(StringComparer.Ordinal);
        var tvdbScope = new HashSet<string>(StringComparer.Ordinal);
        var resolvedTmdb = 0;
        var resolvedTvdb = 0;

        foreach (var row in missingTmdbRows)
        {
            if (!tmdbScope.Add(BuildScopeKey(row))) continue;

            var tvdbId = row.TvdbId ?? 0;
            if (tvdbId <= 0) continue;

            if (!tmdbByTvdb.TryGetValue(tvdbId, out var tmdbId))
            {
                tmdbId = await TryResolveTmdbFromTvdbAsync(tvdbId, ct);
                tmdbByTvdb[tvdbId] = tmdbId;
            }

            if (tmdbId.HasValue && tmdbId.Value > 0)
            {
                _releases.SaveTmdbId(row.ReleaseId, tmdbId.Value);
                resolvedTmdb++;
            }
        }

        foreach (var row in missingTvdbRows)
        {
            if (!tvdbScope.Add(BuildScopeKey(row))) continue;

            var tmdbId = row.TmdbId ?? 0;
            if (tmdbId <= 0) continue;

            if (!tvdbByTmdb.TryGetValue(tmdbId, out var tvdbId))
            {
                tvdbId = await TryResolveTvdbFromTmdbAsync(tmdbId, ct);
                tvdbByTmdb[tmdbId] = tvdbId;
            }

            if (tvdbId.HasValue && tvdbId.Value > 0)
            {
                _releases.SaveTvdbId(row.ReleaseId, tvdbId.Value);
                resolvedTvdb++;
            }
        }

        var propagated = _releases.BackfillEntityExternalIds(lim);
        return new ExternalIdsBackfillResult(
            missingTmdbRows.Count,
            resolvedTmdb,
            missingTvdbRows.Count,
            resolvedTvdb,
            propagated);
    }

    private async Task<int?> TryResolveTmdbFromTvdbAsync(int tvdbId, CancellationToken ct)
    {
        try
        {
            return await _tmdb.GetTvTmdbIdByTvdbIdAsync(tvdbId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Series external-id backfill failed tvdb->tmdb for tvdbId={TvdbId}", tvdbId);
            return null;
        }
    }

    private async Task<int?> TryResolveTvdbFromTmdbAsync(int tmdbId, CancellationToken ct)
    {
        try
        {
            return await _tmdb.GetTvdbIdAsync(tmdbId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Series external-id backfill failed tmdb->tvdb for tmdbId={TmdbId}", tmdbId);
            return null;
        }
    }

    private static string BuildScopeKey(ReleaseRepository.MissingExternalIdsSeedRow row)
    {
        if (row.EntityId.HasValue && row.EntityId.Value > 0)
            return $"e:{row.EntityId.Value}";
        return $"r:{row.ReleaseId}";
    }
}
