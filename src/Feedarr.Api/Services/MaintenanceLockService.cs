namespace Feedarr.Api.Services;

/// <summary>
/// Singleton guard that prevents concurrent execution of heavy SQLite maintenance operations
/// (VACUUM, DetectDuplicates, ReprocessCategories, RebindEntities).
///
/// Each of these operations holds a full-table scan or an exclusive SQLite write lock for
/// extended periods. Running two of them simultaneously would block normal API operation.
///
/// Usage in controllers:
/// <code>
///   if (!_maintenanceLock.TryEnter())
///       return Conflict(new { error = "a maintenance operation is already running" });
///   try { /* heavy work */ }
///   finally { _maintenanceLock.Release(); }
/// </code>
/// </summary>
public sealed class MaintenanceLockService
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// Attempts to acquire the maintenance lock without blocking.
    /// Returns true if the lock was acquired; false if another operation is already running.
    /// </summary>
    public bool TryEnter() => _semaphore.Wait(0);

    /// <summary>Releases the maintenance lock. Must be called in a finally block.</summary>
    public void Release() => _semaphore.Release();
}
