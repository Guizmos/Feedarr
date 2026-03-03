namespace Feedarr.Api.Services.Sync;

public interface ISyncExecutor
{
    Task<SyncExecutionResult> ExecuteAsync(SyncPlan plan, CancellationToken ct);
}
