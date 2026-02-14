using Microsoft.AspNetCore.Http;
using Feedarr.Api.Services.Security;
using System.Diagnostics;

namespace Feedarr.Api.Services.Backup;

public sealed class BackupExecutionCoordinator
{
    private static readonly TimeSpan AcquireTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SyncDrainTimeout = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateLock = new();

    private bool _syncBlocked;
    private int _activeSyncActivities;
    private BackupOperationStateDto _state = new();

    public BackupOperationStateDto GetState()
    {
        lock (_stateLock)
        {
            return new BackupOperationStateDto
            {
                IsBusy = _state.IsBusy,
                Operation = _state.Operation,
                Phase = _state.Phase,
                BackupName = _state.BackupName,
                StartedAtTs = _state.StartedAtTs,
                LastCompletedAtTs = _state.LastCompletedAtTs,
                LastSuccess = _state.LastSuccess,
                LastError = _state.LastError,
                ActiveSyncActivities = _activeSyncActivities,
                SyncBlocked = _syncBlocked
            };
        }
    }

    public IDisposable? TryEnterSyncActivity(string activityName)
    {
        lock (_stateLock)
        {
            if (_syncBlocked)
                return null;

            _activeSyncActivities++;
            return new SyncLease(this, activityName);
        }
    }

    /// <summary>
    /// Async version: acquires the gate and drains sync activities without blocking threads.
    /// The action lambda itself may be synchronous (file I/O).
    /// </summary>
    public async Task<T> RunExclusiveAsync<T>(string operation, string? backupName, Func<T> action)
    {
        if (!await _operationGate.WaitAsync(AcquireTimeout))
            throw new BackupOperationException("backup operation already running", StatusCodes.Status409Conflict);

        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            lock (_stateLock)
            {
                _syncBlocked = true;
                _state.IsBusy = true;
                _state.Operation = operation;
                _state.Phase = "waiting-sync";
                _state.BackupName = backupName;
                _state.StartedAtTs = startedAt.ToUnixTimeSeconds();
                _state.LastError = null;
            }

            await WaitForSyncDrainAsync();

            lock (_stateLock)
            {
                _state.Phase = "running";
            }

            var result = action();

            lock (_stateLock)
            {
                _state.IsBusy = false;
                _state.Operation = "idle";
                _state.Phase = "idle";
                _state.BackupName = null;
                _state.StartedAtTs = null;
                _state.LastCompletedAtTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _state.LastSuccess = true;
                _state.LastError = null;
                _syncBlocked = false;
            }

            return result;
        }
        catch (BackupOperationException ex)
        {
            MarkFailed(ex, "backup operation failed");
            throw;
        }
        catch (Exception ex)
        {
            MarkFailed(ex, "backup operation failed");
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Synchronous wrapper kept for backward compatibility.
    /// Prefer <see cref="RunExclusiveAsync{T}"/> in async controller actions.
    /// </summary>
    public T RunExclusive<T>(string operation, string? backupName, Func<T> action)
    {
        if (!_operationGate.Wait(AcquireTimeout))
            throw new BackupOperationException("backup operation already running", StatusCodes.Status409Conflict);

        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            lock (_stateLock)
            {
                _syncBlocked = true;
                _state.IsBusy = true;
                _state.Operation = operation;
                _state.Phase = "waiting-sync";
                _state.BackupName = backupName;
                _state.StartedAtTs = startedAt.ToUnixTimeSeconds();
                _state.LastError = null;
            }

            WaitForSyncDrain();

            lock (_stateLock)
            {
                _state.Phase = "running";
            }

            var result = action();

            lock (_stateLock)
            {
                _state.IsBusy = false;
                _state.Operation = "idle";
                _state.Phase = "idle";
                _state.BackupName = null;
                _state.StartedAtTs = null;
                _state.LastCompletedAtTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _state.LastSuccess = true;
                _state.LastError = null;
                _syncBlocked = false;
            }

            return result;
        }
        catch (BackupOperationException ex)
        {
            MarkFailed(ex, "backup operation failed");
            throw;
        }
        catch (Exception ex)
        {
            MarkFailed(ex, "backup operation failed");
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task WaitForSyncDrainAsync()
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            int active;
            lock (_stateLock)
            {
                active = _activeSyncActivities;
            }

            if (active <= 0)
                return;

            if (sw.Elapsed >= SyncDrainTimeout)
                throw new BackupOperationException(
                    "sync activities still running, retry in a moment",
                    StatusCodes.Status409Conflict);

            await Task.Delay(100);
        }
    }

    private void WaitForSyncDrain()
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            int active;
            lock (_stateLock)
            {
                active = _activeSyncActivities;
            }

            if (active <= 0)
                return;

            if (sw.Elapsed >= SyncDrainTimeout)
                throw new BackupOperationException(
                    "sync activities still running, retry in a moment",
                    StatusCodes.Status409Conflict);

            Thread.Sleep(100);
        }
    }

    private void MarkFailed(Exception ex, string fallback)
    {
        var safeError = ErrorMessageSanitizer.ToOperationalMessage(ex, fallback);
        MarkFailed(safeError);
    }

    private void MarkFailed(string error)
    {
        var safeError = ErrorMessageSanitizer.Sanitize(error, "operation failed");
        lock (_stateLock)
        {
            _state.IsBusy = false;
            _state.Operation = "idle";
            _state.Phase = "idle";
            _state.BackupName = null;
            _state.StartedAtTs = null;
            _state.LastCompletedAtTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _state.LastSuccess = false;
            _state.LastError = safeError;
            _syncBlocked = false;
        }
    }

    private void ExitSyncActivity()
    {
        lock (_stateLock)
        {
            if (_activeSyncActivities > 0)
                _activeSyncActivities--;
        }
    }

    private sealed class SyncLease : IDisposable
    {
        private readonly BackupExecutionCoordinator _owner;
        private int _disposed;

        public SyncLease(BackupExecutionCoordinator owner, string _)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            _owner.ExitSyncActivity();
        }
    }
}
