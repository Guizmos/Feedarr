using Feedarr.Api.Models;

namespace Feedarr.Api.Services.Sync;

public sealed record SyncPlanInput(
    Source Source,
    SyncEffectiveSettings Settings,
    Dictionary<int, (string key, string label)> CategoryMap,
    IReadOnlyList<int> PersistedCategoryIds,
    IReadOnlyList<int> SelectedCategoryIds,
    IReadOnlyList<int> MappedCategoryIds,
    IReadOnlyList<int> UnmappedCategoryIds,
    long LastSyncAt,
    string CorrelationId,
    string TriggerReason);
