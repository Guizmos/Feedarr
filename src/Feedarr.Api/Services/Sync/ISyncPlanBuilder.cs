namespace Feedarr.Api.Services.Sync;

public interface ISyncPlanBuilder
{
    SyncPlan Build(SyncPlanInput input, SyncPolicy policy);
}
