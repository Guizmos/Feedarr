using Feedarr.Api.Data;
using Feedarr.Api.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Services;

/// <summary>
/// Singleton service owning the storage-usage scan state.
/// Provides async stale-while-revalidate reads with single-flight refreshes.
/// </summary>
public sealed class StorageUsageCacheService
{
    private const string CacheKey = "system:storage-usage:v1";

    private readonly SemaphoreSlim _sem = new(1, 1);
    private readonly IMemoryCache _cache;
    private readonly CancellationToken _appStopping;
    private readonly TimeSpan _cacheDuration;
    private readonly string _dataDirAbs;
    private readonly string _backupDirAbs;
    private readonly string _dbPathAbs;
    private readonly Func<CancellationToken, Task<StorageUsageSnapshot>>? _scanOverride;
    private readonly ILogger<StorageUsageCacheService> _log;

    private volatile bool _everSucceeded;
    private StorageUsageSnapshot _lastSnapshot = new(0, 0, 0, 0, 0, 0);
    private Task<StorageUsageSnapshot>? _inflight;

    /// <summary>Absolute path to the data directory. Exposed for use in Storage endpoint drive enumeration.</summary>
    public string DataDirAbs => _dataDirAbs;

    public StorageUsageCacheService(
        IMemoryCache cache,
        IWebHostEnvironment env,
        IOptions<AppOptions> opts,
        Db db,
        IHostApplicationLifetime appLifetime,
        ILogger<StorageUsageCacheService> log)
        : this(cache, env, opts, db, appLifetime, log, scanOverride: null)
    {
    }

    internal StorageUsageCacheService(
        IMemoryCache cache,
        IWebHostEnvironment env,
        IOptions<AppOptions> opts,
        Db db,
        IHostApplicationLifetime appLifetime,
        ILogger<StorageUsageCacheService> log,
        Func<CancellationToken, Task<StorageUsageSnapshot>>? scanOverride)
    {
        _cache = cache;
        _appStopping = appLifetime.ApplicationStopping;
        _scanOverride = scanOverride;
        _log = log;

        var o = opts.Value;
        _cacheDuration = TimeSpan.FromSeconds(Math.Clamp(o.StorageUsageCacheTtlSeconds, 1, 3600));
        _dataDirAbs = Path.IsPathRooted(o.DataDir)
            ? o.DataDir
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, o.DataDir));
        _backupDirAbs = Path.Combine(_dataDirAbs, "backups");
        _dbPathAbs = Path.Combine(_dataDirAbs, o.DbFileName);
        _ = db;
    }

    /// <summary>
    /// Returns the current storage snapshot.
    /// If a fresh cache entry exists, it is returned immediately.
    /// If the cache expired but a previous scan succeeded, the stale snapshot is returned immediately
    /// and a background refresh is started.
    /// If no snapshot exists yet, the caller awaits the first scan asynchronously.
    /// </summary>
    public async Task<StorageUsageSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey, out StorageUsageSnapshot? hit) && hit is not null)
            return hit;

        var task = await EnsureRefreshScheduledAsync(ct).ConfigureAwait(false);
        if (_everSucceeded)
            return Volatile.Read(ref _lastSnapshot!);

        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<Task<StorageUsageSnapshot>> EnsureRefreshScheduledAsync(CancellationToken ct)
    {
        await _sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(CacheKey, out StorageUsageSnapshot? hit) && hit is not null)
                return Task.FromResult(hit);

            if (_inflight is { IsCompleted: false })
                return _inflight;

            _inflight = RefreshSnapshotAsync(_appStopping);
            return _inflight;
        }
        finally
        {
            _sem.Release();
        }
    }

    private async Task<StorageUsageSnapshot> RefreshSnapshotAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _log.LogDebug("Storage usage scan started");

        try
        {
            var snapshot = _scanOverride is not null
                ? await _scanOverride(ct).ConfigureAwait(false)
                : await ComputeSnapshotAsync(ct).ConfigureAwait(false);

            sw.Stop();

            await _sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _lastSnapshot = snapshot;
                _everSucceeded = true;
                _cache.Set(CacheKey, snapshot, _cacheDuration);
            }
            finally
            {
                _sem.Release();
            }

            _log.LogDebug(
                "Storage usage scan completed in {ElapsedMs} ms — db={DbMb:F1} MB, posters={Posters}, backups={Backups}",
                sw.ElapsedMilliseconds,
                snapshot.DatabaseBytes / 1024.0 / 1024.0,
                snapshot.PostersTopLevelCount,
                snapshot.BackupsCount);

            return snapshot;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            _log.LogDebug("Storage usage scan cancelled after {ElapsedMs} ms", sw.ElapsedMilliseconds);
            return Volatile.Read(ref _lastSnapshot!);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogWarning(ex, "Storage usage scan failed after {ElapsedMs} ms", sw.ElapsedMilliseconds);
            return Volatile.Read(ref _lastSnapshot!);
        }
    }

    private Task<StorageUsageSnapshot> ComputeSnapshotAsync(CancellationToken ct)
    {
        return Task.Run(() =>
        {
            long databaseBytes = 0;
            int postersTopLevelCount = 0;
            int postersRecursiveCount = 0;
            long postersBytes = 0;
            int backupsCount = 0;
            long backupsBytes = 0;

            ct.ThrowIfCancellationRequested();

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
                    // Single pass: derive both top-level count and recursive totals
                    // without a second EnumerateFiles call.
                    var normalizedPostersDir = Path.TrimEndingDirectorySeparator(postersDir);
                    foreach (var file in Directory.EnumerateFiles(postersDir, "*.*", SearchOption.AllDirectories))
                    {
                        ct.ThrowIfCancellationRequested();
                        postersRecursiveCount++;
                        postersBytes += new FileInfo(file).Length;
                        if (string.Equals(
                                Path.GetDirectoryName(file),
                                normalizedPostersDir,
                                StringComparison.OrdinalIgnoreCase))
                            postersTopLevelCount++;
                    }
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
                    foreach (var file in Directory.EnumerateFiles(_backupDirAbs, "*.zip", SearchOption.TopDirectoryOnly))
                    {
                        ct.ThrowIfCancellationRequested();
                        backupsCount++;
                        backupsBytes += new FileInfo(file).Length;
                    }
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
        }, ct);
    }
}

/// <summary>
/// Immutable snapshot of local storage usage (database, posters, backups).
/// </summary>
public sealed record StorageUsageSnapshot(
    long DatabaseBytes,
    int PostersTopLevelCount,
    int PostersRecursiveCount,
    long PostersBytes,
    int BackupsCount,
    long BackupsBytes);
