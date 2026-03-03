namespace Feedarr.Api.Services.Sync;

public sealed record SyncEffectiveSettings(
    int PerCategoryLimit,
    int GlobalLimit,
    int DefaultSeen,
    bool RssOnly,
    bool EnableCategoryFallback,
    bool AllowSearchInitial);
