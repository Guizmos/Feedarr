using System.Globalization;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Metadata;

public sealed record TmdbMetadataBackfillResult(
    int Scanned,
    int Eligible,
    int Processed,
    int LocalPropagated,
    int TmdbRefreshed,
    int UniqueTmdbKeysRefreshed,
    int Skipped,
    int Errors);

public sealed class TmdbMetadataBackfillService
{
    private readonly ReleaseRepository _releases;
    private readonly TmdbClient _tmdb;
    private readonly ILogger<TmdbMetadataBackfillService> _logger;

    public TmdbMetadataBackfillService(
        ReleaseRepository releases,
        TmdbClient tmdb,
        ILogger<TmdbMetadataBackfillService> logger)
    {
        _releases = releases;
        _tmdb = tmdb;
        _logger = logger;
    }

    public async Task<TmdbMetadataBackfillResult> BackfillMissingTmdbMetadataAsync(int limit, CancellationToken ct)
    {
        var lim = Math.Clamp(limit <= 0 ? 200 : limit, 1, 5000);
        var seeds = _releases.GetTmdbMetadataMissingCandidates(lim);
        if (seeds.Count == 0)
            return new TmdbMetadataBackfillResult(0, 0, 0, 0, 0, 0, 0, 0);

        var scopedSeeds = seeds
            .GroupBy(BuildScopeKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        var tmdbIds = scopedSeeds
            .Select(seed => seed.TmdbId)
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        var donors = _releases.GetTmdbMetadataDonorsByTmdbIds(tmdbIds);

        var donorByKey = donors
            .GroupBy(BuildMetadataKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);

        var fallbackByKey = new Dictionary<string, List<ReleaseRepository.MissingTmdbMetadataSeedRow>>(StringComparer.Ordinal);
        var localPropagated = 0;
        var tmdbRefreshed = 0;
        var errors = 0;

        foreach (var seed in scopedSeeds)
        {
            ct.ThrowIfCancellationRequested();
            var metadataKey = BuildMetadataKey(seed.TmdbId, seed.MediaTypeNormalized);
            if (donorByKey.TryGetValue(metadataKey, out var donor))
            {
                if (TryApplyExternalDetails(
                    seed.ReleaseId,
                    provider: donor.ExtProvider ?? "tmdb",
                    providerId: donor.ExtProviderId ?? seed.TmdbId.ToString(CultureInfo.InvariantCulture),
                    title: donor.ExtTitle,
                    overview: donor.ExtOverview,
                    tagline: donor.ExtTagline,
                    genres: donor.ExtGenres,
                    releaseDate: donor.ExtReleaseDate,
                    runtimeMinutes: donor.ExtRuntimeMinutes,
                    rating: donor.ExtRating,
                    votes: donor.ExtVotes,
                    directors: donor.ExtDirectors,
                    writers: donor.ExtWriters,
                    cast: donor.ExtCast))
                {
                    localPropagated++;
                }
                else
                {
                    errors++;
                }

                continue;
            }

            if (!fallbackByKey.TryGetValue(metadataKey, out var group))
            {
                group = new List<ReleaseRepository.MissingTmdbMetadataSeedRow>();
                fallbackByKey[metadataKey] = group;
            }

            group.Add(seed);
        }

        var uniqueTmdbKeysRefreshed = 0;
        foreach (var (_, group) in fallbackByKey)
        {
            ct.ThrowIfCancellationRequested();
            var representative = group[0];
            TmdbClient.DetailsResult? details;

            try
            {
                details = representative.MediaTypeNormalized == "series"
                    ? await _tmdb.GetTvDetailsAsync(representative.TmdbId, ct).ConfigureAwait(false)
                    : await _tmdb.GetMovieDetailsAsync(representative.TmdbId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "TMDB metadata backfill failed for tmdbId={TmdbId} mediaType={MediaType}",
                    representative.TmdbId,
                    representative.MediaTypeNormalized);
                errors++;
                continue;
            }

            if (details is null)
                continue;

            uniqueTmdbKeysRefreshed++;
            foreach (var seed in group)
            {
                if (TryApplyExternalDetails(
                    seed.ReleaseId,
                    provider: "tmdb",
                    providerId: seed.TmdbId.ToString(CultureInfo.InvariantCulture),
                    title: details.Title,
                    overview: details.Overview,
                    tagline: details.Tagline,
                    genres: details.Genres,
                    releaseDate: details.ReleaseDate,
                    runtimeMinutes: details.RuntimeMinutes,
                    rating: details.Rating,
                    votes: details.Votes,
                    directors: details.Directors,
                    writers: details.Writers,
                    cast: details.Cast))
                {
                    tmdbRefreshed++;
                }
                else
                {
                    errors++;
                }
            }
        }

        var processed = localPropagated + tmdbRefreshed;
        var skipped = Math.Max(0, scopedSeeds.Count - processed);
        return new TmdbMetadataBackfillResult(
            seeds.Count,
            scopedSeeds.Count,
            processed,
            localPropagated,
            tmdbRefreshed,
            uniqueTmdbKeysRefreshed,
            skipped,
            errors);
    }

    private bool TryApplyExternalDetails(
        long releaseId,
        string provider,
        string providerId,
        string? title,
        string? overview,
        string? tagline,
        string? genres,
        string? releaseDate,
        int? runtimeMinutes,
        double? rating,
        int? votes,
        string? directors,
        string? writers,
        string? cast)
    {
        try
        {
            _releases.UpdateExternalDetails(
                releaseId,
                provider,
                providerId,
                title,
                overview,
                tagline,
                genres,
                releaseDate,
                runtimeMinutes,
                rating,
                votes,
                directors,
                writers,
                cast);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TMDB metadata backfill apply failed for releaseId={ReleaseId}", releaseId);
            return false;
        }
    }

    private static string BuildScopeKey(ReleaseRepository.MissingTmdbMetadataSeedRow row)
    {
        if (row.EntityId.HasValue && row.EntityId.Value > 0)
            return $"e:{row.EntityId.Value}";
        return $"r:{row.ReleaseId}";
    }

    private static string BuildMetadataKey(ReleaseRepository.MissingTmdbMetadataSeedRow row)
        => BuildMetadataKey(row.TmdbId, row.MediaTypeNormalized);

    private static string BuildMetadataKey(ReleaseRepository.TmdbMetadataDonorRow row)
        => BuildMetadataKey(row.TmdbId, row.MediaTypeNormalized);

    private static string BuildMetadataKey(int tmdbId, string mediaTypeNormalized)
        => $"{tmdbId}:{mediaTypeNormalized}";
}
