namespace Feedarr.Api.Services.Backup;

public sealed class BackupOperationStateDto
{
    public RestorePreparationStatus? Preparation { get; set; }
    public PendingRestoreStatus? RestorePending { get; set; }
    public bool IsBusy { get; set; }
    public bool NeedsRestart { get; set; }
    public string Operation { get; set; } = "idle";
    public string Phase { get; set; } = "idle";
    public string? BackupName { get; set; }
    public long? StartedAtTs { get; set; }
    public long? LastCompletedAtTs { get; set; }
    public bool? LastSuccess { get; set; }
    public string? LastError { get; set; }
    public int ActiveSyncActivities { get; set; }
    public bool SyncBlocked { get; set; }
}

public sealed record RestorePreparationStatus(string Id, string State, string? Error = null, BackupRestoreResult? Result = null);
