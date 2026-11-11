using System.Text.RegularExpressions;
using Feedarr.Api.Services.Posters;

namespace Feedarr.Api.Services;

public sealed partial class RetentionService
{
    internal const string HistoryConfirmation = "RECLAIM_UNUSED_HISTORY";
    private static readonly Regex OwnedLegacyPoster = new(
        @"^(?:tmdb-[1-9][0-9]*-w500|(?:igdb|rawg)-[1-9][0-9]*-cover|tvmaze-[1-9][0-9]*-(?:original|medium)|(?:fanart|jikan|comicvine|theaudiodb)-[1-9][0-9]*)\.(?:jpg|jpeg|png|webp)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    internal Action<string>? HistoryFaultPoint { get; set; }

    public Task<HistoryCleanupReport> CleanupHistoryAsync(HistoryCleanupRequest request, CancellationToken ct = default)
    {
        if (request.Limit is < 1 or > 100 || request.ScanLimit is < 1 or > 5000)
            throw new ArgumentException("Limit must be 1..100; scanLimit must be 1..5000.");
        if (_posters is null) throw new InvalidOperationException("Poster storage is unavailable.");
        if (request.DryRun) return Task.FromResult(CleanupHistoryCore(request, ct));
        if (request.Confirmation != HistoryConfirmation)
            throw new ArgumentException("Explicit history cleanup confirmation is required.");
        if (_historyCoordinator.GetState().IsBusy)
            throw new InvalidOperationException("Backup/restore operation in progress; retry later.");
        return _historyCoordinator.RunExclusiveAsync("history-cleanup", null,
            token => Task.FromResult(CleanupHistoryCore(request, token)), ct, blockSync: false);
    }

    private bool HistoryRestoreProtected()
    {
        if (Backup.Configuration.ConfigurationRestorePlanStore.HasPlan(
                Path.GetDirectoryName(_posters!.PostersDirPath)!)) return true;
        var root = Path.Combine(Path.GetDirectoryName(_posters!.PostersDirPath)!, "restore-state");
        return File.Exists(Path.Combine(root, "pending.json")) || Directory.Exists(Path.Combine(root, "pending.json")) ||
               File.Exists(Path.Combine(root, "operation.json")) || Directory.Exists(Path.Combine(root, "operation.json")) ||
               File.Exists(Path.Combine(root, "pending.json.tmp")) || File.Exists(Path.Combine(root, "operation.json.tmp")) ||
               File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "restore.restart-required.flag")) ||
               (Directory.Exists(Path.Combine(root, "stages")) && Directory.EnumerateDirectories(Path.Combine(root, "stages")).Any()) ||
               (Directory.Exists(root) && Directory.EnumerateDirectories(root, "building-*").Any());
    }

    private HistoryCleanupReport CleanupHistoryCore(HistoryCleanupRequest request, CancellationToken ct)
    {
        var report = new HistoryCleanupReport { DryRun = request.DryRun, WindowAfterPoster = request.AfterPoster, WindowAfterStore = request.AfterStore, PublicationBusy = _releases.PosterLifecycle.HasPublications };
        if (!request.DryRun) _log.LogInformation("Explicit bounded history cleanup started limit={Limit}", request.Limit);
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddDays(-30).ToUnixTimeSeconds();
        var reasons = _releases.HistoryEntityCounts(cutoff);
        report.EntitiesBefore = reasons.Values.Sum();
        report.EntityCandidates = reasons.GetValueOrDefault("eligible");
        report.ProtectedEntities = report.EntitiesBefore - report.EntityCandidates;
        foreach (var (reason, count) in reasons.Where(row => row.Key != "eligible")) report.ProtectionReasons["entity:" + reason] = count;
        var entities = _releases.HistoryEntityCandidates(cutoff, request.Limit);
        report.EntityBatchCandidates = entities.Count;
        var root = _posters!.PostersDirPath;
        if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Symlink poster root is not eligible for cleanup.");
        foreach (var file in entities.Select(row => row.PosterFile).Where(file => !string.IsNullOrWhiteSpace(file)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (new PosterPathResolver(root).TryResolvePosterFile(file!, out var path) && !File.Exists(path)) report.MissingPosterFiles++;

        report.RestoreProtected = HistoryRestoreProtected();
        if (!request.DryRun && !report.RestoreProtected)
        {
            foreach (var batch in entities.Chunk(10))
            {
                ct.ThrowIfCancellationRequested();
                using var lease = _releases.PosterLifecycle.TryEnterCollection();
                if (lease is null) { report.PublicationBusy = true; break; }
                if (HistoryRestoreProtected()) { report.RestoreProtected = true; break; }
                HistoryFaultPoint?.Invoke("before-entity-revalidation");
                try { report.EntitiesDeleted += _releases.DeleteHistoryEntities(batch.Select(row => row.Id).ToArray(), cutoff); }
                catch (Exception ex)
                {
                    report.Errors++;
                    _log.LogWarning(ex, "History entity cleanup batch failed; stopping this pass");
                    break;
                }
            }
        }
        var projected = request.DryRun && !report.RestoreProtected ? entities.Select(row => row.Id).ToArray() : [];
        var referenced = _releases.HistoryReferencedPosterFiles(projected);
        if (Directory.Exists(root))
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Symlink poster root is not eligible for cleanup.");
            var files = HistoryPosterWindow(root, request.AfterPoster, request.ScanLimit, ct);
            foreach (var file in files.Take(request.ScanLimit))
            {
                ct.ThrowIfCancellationRequested();
                report.PostersScanned++;
                var full = Path.Combine(root, file);
                var protectedReason = "";
                try
                {
                    var info = new FileInfo(full);
                    if (!info.Exists) { report.MissingPosterFiles++; continue; }
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) protectedReason = "symlink";
                    else if (info.Length <= 0) protectedReason = "empty_or_nonregular_file";
                    else if (!OwnedLegacyPoster.IsMatch(file)) protectedReason = "unknown_or_manual_file";
                    else if (referenced.Contains(file)) protectedReason = "database_reference";
                    else if (info.LastWriteTimeUtc > now.UtcDateTime.AddDays(-30)) protectedReason = "file_grace_period";
                    else if (report.RestoreProtected) protectedReason = "pending_restore";
                    if (protectedReason.Length > 0)
                    {
                        report.ProtectedPosters++;
                        AddHistoryReason(report, "poster:" + protectedReason);
                        continue;
                    }
                    report.PosterCandidates++;
                    report.EstimatedRecoverableBytes += info.Length;
                    if (request.DryRun || report.PosterDeleteAttempts >= request.Limit) continue;
                    report.PosterDeleteAttempts++;
                    using var lease = _releases.PosterLifecycle.TryEnterCollection();
                    if (lease is null) { report.PublicationBusy = true; report.ProtectedPosters++; AddHistoryReason(report, "poster:active_publication"); continue; }
                    if (HistoryRestoreProtected()) { report.RestoreProtected = true; report.ProtectedPosters++; AddHistoryReason(report, "poster:pending_restore"); continue; }
                    HistoryFaultPoint?.Invoke("before-poster-revalidation");
                    // Re-read filesystem age and references after acquiring the publication lease.
                    info.Refresh();
                    if (!info.Exists) { report.MissingPosterFiles++; continue; }
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length <= 0 || info.LastWriteTimeUtc > now.UtcDateTime.AddDays(-30))
                    { report.ProtectedPosters++; AddHistoryReason(report, "poster:changed_before_delete"); continue; }
                    var bytes = info.Length;
                    if (_releases.DeleteHistoryPosterIfUnreferenced(file, () =>
                    {
                        _fileStore.Delete(full);
                        if (_fileStore.Exists(full)) throw new IOException("Legacy poster still exists after deletion.");
                    }, audit: true))
                    { report.PostersDeleted++; report.ReclaimedBytes += bytes; }
                    else { report.ProtectedPosters++; AddHistoryReason(report, "poster:referenced_before_delete"); }
                }
                catch (OperationCanceledException) { throw; }
                catch (FileNotFoundException) { report.MissingPosterFiles++; }
                catch (DirectoryNotFoundException) { report.MissingPosterFiles++; }
                catch (Exception ex)
                {
                    report.Errors++;
                    if (!request.DryRun) _log.LogWarning(ex, "History poster cleanup failed");
                }
            }
            if (files.Length > request.ScanLimit) report.NextPoster = files[request.ScanLimit - 1];
        }
        var storeRefs = _releases.HistoryStoreReferences();
        report.DeclaredStoreReferences = storeRefs.Count;
        var stores = storeRefs.GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => request.AfterStore is null || string.CompareOrdinal(group.Key, request.AfterStore) > 0)
            .OrderBy(group => group.Key, StringComparer.Ordinal).Take(request.ScanLimit + 1).ToArray();
        var resolver = new PosterStorePathResolver(_posters.PosterStoreDirPath);
        foreach (var store in stores.Take(request.ScanLimit))
        {
            ct.ThrowIfCancellationRequested();
            report.StoresScanned++;
            if (!resolver.TryResolveStoreDir(store.Key, out var full)) { report.Errors++; continue; }
            if (!Directory.Exists(full)) { report.MissingStoreDirectories++; report.MissingStoreReferenceRows += store.Count(); }
        }
        if (stores.Length > request.ScanLimit) report.NextStore = stores[request.ScanLimit - 1].Key;
        report.EntitiesAfter = request.DryRun ? report.EntitiesBefore : _releases.HistoryEntityCounts(cutoff).Values.Sum();
        if (!request.DryRun) _log.LogInformation("History cleanup entities={Entities} posters={Posters} bytes={Bytes} errors={Errors}",
            report.EntitiesDeleted, report.PostersDeleted, report.ReclaimedBytes, report.Errors);
        return report;
    }

    private static string[] HistoryPosterWindow(string root, string? after, int limit, CancellationToken ct)
    {
        // Enumerate names but retain only a bounded window in memory; stat/content reads
        // are limited to that window, even for a very large historical directory.
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (after is not null && string.CompareOrdinal(name, after) <= 0) continue;
            names.Add(name);
            if (names.Count > limit + 1) names.Remove(names.Max!);
        }
        return names.ToArray();
    }

    private static void AddHistoryReason(HistoryCleanupReport report, string reason)
        => report.ProtectionReasons[reason] = report.ProtectionReasons.GetValueOrDefault(reason) + 1;
}
