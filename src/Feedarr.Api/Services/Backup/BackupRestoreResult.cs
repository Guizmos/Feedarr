namespace Feedarr.Api.Services.Backup;

public sealed class BackupRestoreResult
{
    public int ReencryptedCredentials { get; init; }
    public int ClearedUndecryptableCredentials { get; init; }
}
