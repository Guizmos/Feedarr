namespace Feedarr.Api.Services.Backup;

public sealed class BackupRestoreResult
{
    public int MissingPosters { get; init; }
    public int ReencryptedCredentials { get; init; }
    public int ClearedUndecryptableCredentials { get; init; }
}
