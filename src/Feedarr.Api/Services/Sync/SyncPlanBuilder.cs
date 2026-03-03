using Feedarr.Api.Services.Categories;

namespace Feedarr.Api.Services.Sync;

public sealed class SyncPlanBuilder : ISyncPlanBuilder
{
    public SyncPlan Build(SyncPlanInput input, SyncPolicy policy)
    {
        var selectionReason = CategorySelectionAudit.InferReason(
            persistedWasNull: false,
            persistedCount: input.PersistedCategoryIds.Count,
            parseErrorCount: 0,
            mappedCount: input.PersistedCategoryIds.Count);

        var selectedUnifiedKeys = new HashSet<string>(
            input.CategoryMap.Values
                .Select(value => value.key)
                .Where(key => !string.IsNullOrWhiteSpace(key)),
            StringComparer.OrdinalIgnoreCase);

        return new SyncPlan(
            input,
            new FetchPlan(
                PerCategoryLimit: input.Settings.PerCategoryLimit,
                RssOnly: input.Settings.RssOnly,
                EnableCategoryFallback: input.Settings.EnableCategoryFallback && policy.EnableCategoryFallback,
                AllowSearchInitial: input.Settings.AllowSearchInitial && policy.AllowSearchInitial),
            new FilterPlan(
                PersistedCategoryIds: input.PersistedCategoryIds,
                SelectedCategoryIds: input.SelectedCategoryIds,
                MappedCategoryIds: input.MappedCategoryIds,
                UnmappedCategoryIds: input.UnmappedCategoryIds,
                SelectedUnifiedKeys: selectedUnifiedKeys.ToList(),
                SelectionReason: selectionReason,
                CategoryMap: input.CategoryMap),
            new DbPlan(
                DefaultSeen: input.Settings.DefaultSeen,
                GlobalLimit: input.Settings.GlobalLimit),
            new PosterPlan(
                SelectionMode: policy.PosterSelectionMode,
                LastSyncAt: input.LastSyncAt,
                ForceRefresh: false),
            new TelemetryPlan(
                CorrelationId: input.CorrelationId,
                LogPrefix: policy.LogPrefix,
                TriggerReason: input.TriggerReason,
                RecordIndexerQuery: policy.RecordIndexerQuery,
                RecordPerSourceSyncJob: policy.RecordPerSourceSyncJob,
                EmitCategoryDebugActivity: policy.EmitCategoryDebugActivity));
    }
}
