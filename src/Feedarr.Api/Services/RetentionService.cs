using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services.Posters;

namespace Feedarr.Api.Services;

public sealed class RetentionService
{
    private readonly ReleaseRepository _releases;
    private readonly PosterFetchService _posters;
    private readonly ILogger<RetentionService> _log;

    public RetentionService(
        ReleaseRepository releases,
        PosterFetchService posters,
        ILogger<RetentionService> log)
    {
        _releases = releases;
        _posters = posters;
        _log = log;
    }

    public (ReleaseRepository.RetentionResult result, int postersPurged) ApplyRetention(
        long sourceId,
        int perCatLimit,
        int globalLimit)
    {
        var result = _releases.ApplyRetention(sourceId, perCatLimit, globalLimit);
        var postersPurged = CleanupOrphanPosters(result.PosterFiles);
        return (result, postersPurged);
    }

    private int CleanupOrphanPosters(IEnumerable<string> posterFiles)
    {
        if (posterFiles is null) return 0;
        var unique = posterFiles
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (unique.Count == 0) return 0;

        var postersDir = _posters.PostersDirPath;
        var purged = 0;

        foreach (var file in unique)
        {
            var refCount = _releases.GetPosterReferenceCount(file);
            if (refCount > 0) continue;

            try
            {
                var full = Path.GetFullPath(Path.Combine(postersDir, file));
                var root = Path.GetFullPath(postersDir);
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (File.Exists(full))
                {
                    File.Delete(full);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Poster delete failed file={File}", file);
            }

            _releases.ClearPosterFileReferences(file);
            purged++;
        }

        return purged;
    }
}
