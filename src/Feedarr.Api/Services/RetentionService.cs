using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services.Posters;

namespace Feedarr.Api.Services;

public sealed class RetentionService
{
    private readonly ReleaseRepository _releases;
    private readonly PosterFetchService _posters;
    private readonly IPosterFileStore _fileStore;
    private readonly ILogger<RetentionService> _log;

    public RetentionService(
        ReleaseRepository releases,
        PosterFetchService posters,
        IPosterFileStore fileStore,
        ILogger<RetentionService> log)
    {
        _releases = releases;
        _posters = posters;
        _fileStore = fileStore;
        _log = log;
    }

    public (ReleaseRepository.RetentionResult result, int postersPurged, int failedDeletes) ApplyRetention(
        long sourceId,
        int perCatLimit,
        int globalLimit)
    {
        var result = _releases.ApplyRetention(sourceId, perCatLimit, globalLimit);
        var (postersPurged, failedDeletes) = CleanupOrphanPosters(result.PosterFiles);
        return (result, postersPurged, failedDeletes);
    }

    private (int purged, int failedDeletes) CleanupOrphanPosters(IEnumerable<string> posterFiles)
    {
        if (posterFiles is null) return (0, 0);
        var unique = posterFiles
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (unique.Count == 0) return (0, 0);

        var pathResolver = new PosterPathResolver(_posters.PostersDirPath);
        var purged = 0;
        var failedDeletes = 0;
        var referencedPosters = _releases.GetReferencedPosterFiles(unique);

        foreach (var file in unique)
        {
            if (referencedPosters.Contains(file)) continue;

            if (!pathResolver.TryResolvePosterFile(file, out var full))
            {
                failedDeletes++;
                _log.LogWarning("Poster delete skipped for unsafe file={File}", file);
                continue;
            }

            try
            {
                if (_fileStore.Exists(full))
                {
                    _fileStore.Delete(full);
                    if (_fileStore.Exists(full))
                    {
                        failedDeletes++;
                        _log.LogWarning("Poster delete failed file={File}", file);
                        continue;
                    }
                }

                _releases.ClearPosterFileReferences(file);
                purged++;
            }
            catch (Exception ex)
            {
                failedDeletes++;
                _log.LogWarning(ex, "Poster delete failed file={File}", file);
            }
        }

        return (purged, failedDeletes);
    }
}
