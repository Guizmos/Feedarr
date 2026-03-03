namespace Feedarr.Api.Services.Sync;

public sealed record FetchPlan(
    int PerCategoryLimit,
    bool RssOnly,
    bool EnableCategoryFallback,
    bool AllowSearchInitial);

public sealed record FilterPlan(
    IReadOnlyList<int> PersistedCategoryIds,
    IReadOnlyList<int> SelectedCategoryIds,
    IReadOnlyList<int> MappedCategoryIds,
    IReadOnlyList<int> UnmappedCategoryIds,
    IReadOnlyCollection<string> SelectedUnifiedKeys,
    string SelectionReason,
    Dictionary<int, (string key, string label)> CategoryMap);

public sealed record DbPlan(
    int DefaultSeen,
    int GlobalLimit);

public sealed record PosterPlan(
    PosterSelectionMode SelectionMode,
    long LastSyncAt,
    bool ForceRefresh);

public sealed record TelemetryPlan(
    string CorrelationId,
    string LogPrefix,
    string TriggerReason,
    bool RecordIndexerQuery,
    bool RecordPerSourceSyncJob,
    bool EmitCategoryDebugActivity);

public sealed record SyncPlan(
    SyncPlanInput Input,
    FetchPlan Fetch,
    FilterPlan Filter,
    DbPlan Db,
    PosterPlan Poster,
    TelemetryPlan Telemetry);
