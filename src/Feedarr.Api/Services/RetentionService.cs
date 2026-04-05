using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services.Posters;

namespace Feedarr.Api.Services;

public sealed class RetentionService
{
    private const long FullScanIntervalSeconds = 3600; // at most once per hour

    private readonly ReleaseRepository _releases;
    private readonly PosterFetchService? _posters;
    private readonly IPosterFileStore _fileStore;
    private readonly ILogger<RetentionService> _log;
    private long _lastFullScanTs;

    public RetentionService(
        ReleaseRepository releases,
        PosterFetchService? posters,
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
        var (postersPurged, failedPosterDeletes) = CleanupOrphanPosters(result.PosterFiles);
        var (storeDirsPurged, failedStoreDeletes) = CleanupOrphanStoreDirs(result.StoreDirs);
        if (storeDirsPurged > 0 || failedStoreDeletes > 0)
        {
            _log.LogInformation(
                "Poster store retention cleanup completed storeDirsPurged={StoreDirsPurged} failedStoreDeletes={FailedStoreDeletes}",
                storeDirsPurged,
                failedStoreDeletes);
        }

        // Periodic full-scan GC: catches orphaned store dirs created by poster
        // updates / rebinds that are not covered by the targeted retention pass.
        TryRunPeriodicFullScanGc();

        return (result, postersPurged, failedPosterDeletes + failedStoreDeletes);
    }

    private (int purged, int failedDeletes) CleanupOrphanPosters(IEnumerable<string> posterFiles)
    {
        if (posterFiles is null) return (0, 0);
        if (_posters is null) return (0, 0);
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

    /// <summary>
    /// Runs a full filesystem scan of the poster store directory and deletes any
    /// orphaned subdirectories.  Throttled to at most once per hour so that
    /// frequent sync runs do not incur repeated filesystem scans.
    /// </summary>
    private void TryRunPeriodicFullScanGc()
    {
        if (_posters is null) return;

        var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var lastScan = Interlocked.Read(ref _lastFullScanTs);
        if (nowTs - lastScan < FullScanIntervalSeconds) return;

        // Only one thread wins the CAS; others skip this cycle.
        if (Interlocked.CompareExchange(ref _lastFullScanTs, nowTs, lastScan) != lastScan) return;

        var storeRoot = _posters.PosterStoreDirPath;
        if (!Directory.Exists(storeRoot)) return;

        List<string> allDirs;
        try
        {
            allDirs = Directory.GetDirectories(storeRoot, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Poster store full-scan GC: failed to enumerate directories");
            return;
        }

        if (allDirs.Count == 0) return;

        var (purged, failed) = CleanupOrphanStoreDirs(allDirs);
        if (purged > 0 || failed > 0)
            _log.LogInformation(
                "Poster store full-scan GC completed purged={Purged} failed={Failed}", purged, failed);
    }

    private (int purged, int failedDeletes) CleanupOrphanStoreDirs(IEnumerable<string> candidateStoreDirs)
    {
        if (candidateStoreDirs is null) return (0, 0);
        if (_posters is null) return (0, 0);

        var unique = candidateStoreDirs
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unique.Count == 0) return (0, 0);

        var resolver = new PosterStorePathResolver(_posters.PosterStoreDirPath);
        var orphaned = _releases.GetOrphanedStoreDirs(unique);
        if (orphaned.Count == 0) return (0, 0);

        var purged = 0;
        var failedDeletes = 0;

        foreach (var dir in orphaned)
        {
            if (!resolver.TryResolveStoreDir(dir, out var fullPath))
            {
                failedDeletes++;
                _log.LogWarning("Poster store delete skipped for unsafe dir={StoreDir}", dir);
                continue;
            }

            try
            {
                if (Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, recursive: true);
                    if (Directory.Exists(fullPath))
                    {
                        failedDeletes++;
                        _log.LogWarning("Poster store delete failed dir={StoreDir}", dir);
                        continue;
                    }
                }

                purged++;
            }
            catch (Exception ex)
            {
                failedDeletes++;
                _log.LogWarning(ex, "Poster store delete failed dir={StoreDir}", dir);
            }
        }

        return (purged, failedDeletes);
    }
}
