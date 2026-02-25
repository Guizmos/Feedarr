using Feedarr.Api.Data;
using Feedarr.Api.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Services;

/// <summary>
/// Singleton service owning the storage-usage scan state.
/// Replaces the static mutable fields previously embedded in SystemController.
///
/// Guarantees:
///   - Single-flight: only one filesystem scan runs at a time.
///   - Stale-while-revalidate: after the first successful scan, callers receive
///     the cached snapshot immediately while a background refresh runs silently.
///   - First-scan wait: on the very first call (no prior data), callers wait up
///     to 5 seconds so start-up requests get a real value instead of zeros.
/// </summary>
public sealed class StorageUsageCacheService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ScanCooldown = TimeSpan.FromSeconds(30);
    private const string CacheKey = "system:storage-usage:v1";

    // SemaphoreSlim(1,1): non-reentrant, guards mutable scan state.
    // We never do I/O inside the semaphore — only field reads/writes and cache.Set.
    private readonly SemaphoreSlim _sem = new(1, 1);
    private readonly IMemoryCache _cache;
    private readonly string _dataDirAbs;
    private readonly string _backupDirAbs;
    private readonly string _dbPathAbs;
    private readonly ILogger<StorageUsageCacheService> _log;

    // _everSucceeded: volatile so the stale-while-revalidate fast path
    // (outside the semaphore) reads it with acquire semantics.
    // _lastSnapshot: written before _everSucceeded = true (release order via
    // semaphore.Release), so a thread seeing _everSucceeded == true is
    // guaranteed to see the corresponding _lastSnapshot.
    private volatile bool _everSucceeded;
    private StorageUsageSnapshot _lastSnapshot = new(0, 0, 0, 0, 0, 0);
    private Task<StorageUsageSnapshot>? _inflight;
    private DateTime _lastScanStartedUtc = DateTime.MinValue;

    /// <summary>Absolute path to the data directory. Exposed for use in Storage endpoint drive enumeration.</summary>
    public string DataDirAbs => _dataDirAbs;

    public StorageUsageCacheService(
        IMemoryCache cache,
        IWebHostEnvironment env,
        IOptions<AppOptions> opts,
        Db db,
        ILogger<StorageUsageCacheService> log)
    {
        _cache = cache;
        _log = log;
        var o = opts.Value;
        _dataDirAbs = Path.IsPathRooted(o.DataDir)
            ? o.DataDir
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, o.DataDir));
        _backupDirAbs = Path.Combine(_dataDirAbs, "backups");
        _dbPathAbs = Path.Combine(_dataDirAbs, o.DbFileName);
    }

    /// <summary>
    /// Returns a storage usage snapshot, using stale-while-revalidate semantics.
    /// Never blocks longer than ~5 seconds (first-ever scan only).
    /// </summary>
    public StorageUsageSnapshot GetSnapshot()
    {
        // Hot path: IMemoryCache is thread-safe for concurrent reads.
        if (_cache.TryGetValue(CacheKey, out StorageUsageSnapshot? hit) && hit is not null)
            return hit;

        // Volatile read BEFORE acquiring the semaphore so we can skip the
        // lock on the stale-while-revalidate fast path.
        bool hasStale = _everSucceeded;

        _sem.Wait();
        Task<StorageUsageSnapshot>? task;
        try { task = EnsureInflightLocked(); }
        finally { _sem.Release(); }

        // Stale-while-revalidate: if a prior scan succeeded, return that value
        // immediately so callers never block after the first scan.
        if (hasStale)
            return Volatile.Read(ref _lastSnapshot!);

        // First scan ever: block up to 5 s so start-up callers receive real data.
        // All concurrent first-callers share the same in-flight Task (single-flight).
        if (task is not null && task.Wait(TimeSpan.FromSeconds(5)))
            return task.Result;

        // Timed out or no task (cooldown with no prior data) — return zeros.
        return Volatile.Read(ref _lastSnapshot!);
    }

    /// <summary>Must be called with _sem held.</summary>
    private Task<StorageUsageSnapshot>? EnsureInflightLocked()
    {
        // a) Scan already in flight — share the existing Task (single-flight).
        if (_inflight is { IsCompleted: false })
            return _inflight;

        // b) Cooldown active — don't hammer the filesystem on every cache miss.
        if (DateTime.UtcNow - _lastScanStartedUtc < ScanCooldown)
            return _inflight; // may be a completed task or null

        // c) Start a new background scan.
        _lastScanStartedUtc = DateTime.UtcNow;
        _inflight = Task.Run(RunScan);
        return _inflight;
    }

    private StorageUsageSnapshot RunScan()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _log.LogDebug("Storage usage scan started");

        try
        {
            var snapshot = ComputeSnapshot();
            sw.Stop();

            _sem.Wait();
            try
            {
                // Write snapshot BEFORE flipping _everSucceeded so any thread
                // that subsequently reads _everSucceeded == true (volatile acquire)
                // is guaranteed to observe the new snapshot.
                _lastSnapshot = snapshot;
                _everSucceeded = true;
                _cache.Set(CacheKey, snapshot, CacheDuration);
            }
            finally { _sem.Release(); }

            _log.LogDebug(
                "Storage usage scan completed in {ElapsedMs} ms " +
                "— db={DbMb:F1} MB, posters={Posters}, backups={Backups}",
                sw.ElapsedMilliseconds,
                snapshot.DatabaseBytes / 1024.0 / 1024.0,
                snapshot.PostersTopLevelCount,
                snapshot.BackupsCount);

            return snapshot;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogWarning(ex, "Storage usage scan failed after {ElapsedMs} ms", sw.ElapsedMilliseconds);
            return Volatile.Read(ref _lastSnapshot!);
        }
    }

    private StorageUsageSnapshot ComputeSnapshot()
    {
        long databaseBytes = 0;
        int postersTopLevelCount = 0;
        int postersRecursiveCount = 0;
        long postersBytes = 0;
        int backupsCount = 0;
        long backupsBytes = 0;

        try
        {
            if (File.Exists(_dbPathAbs))
                databaseBytes = new FileInfo(_dbPathAbs).Length;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read database file size at {DbPath}", _dbPathAbs);
        }

        var postersDir = Path.Combine(_dataDirAbs, "posters");
        try
        {
            if (Directory.Exists(postersDir))
            {
                postersTopLevelCount = Directory.GetFiles(postersDir, "*.*", SearchOption.TopDirectoryOnly).Length;
                var files = Directory.GetFiles(postersDir, "*.*", SearchOption.AllDirectories);
                postersRecursiveCount = files.Length;
                postersBytes = files.Sum(f => new FileInfo(f).Length);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to calculate posters directory size at {PostersDir}", postersDir);
        }

        try
        {
            if (Directory.Exists(_backupDirAbs))
            {
                var files = Directory.GetFiles(_backupDirAbs, "*.zip", SearchOption.TopDirectoryOnly);
                backupsCount = files.Length;
                backupsBytes = files.Sum(f => new FileInfo(f).Length);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to calculate backups directory size at {BackupDir}", _backupDirAbs);
        }

        return new StorageUsageSnapshot(
            databaseBytes,
            postersTopLevelCount,
            postersRecursiveCount,
            postersBytes,
            backupsCount,
            backupsBytes);
    }
}

/// <summary>
/// Immutable snapshot of local storage usage (database, posters, backups).
/// Extracted from SystemController to be shared with StorageUsageCacheService.
/// </summary>
public sealed record StorageUsageSnapshot(
    long DatabaseBytes,
    int PostersTopLevelCount,
    int PostersRecursiveCount,
    long PostersBytes,
    int BackupsCount,
    long BackupsBytes);
