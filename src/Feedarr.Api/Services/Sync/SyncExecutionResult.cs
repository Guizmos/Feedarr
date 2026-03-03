namespace Feedarr.Api.Services.Sync;

public sealed record SyncExecutionResult(
    bool Ok,
    long SourceId,
    string SourceName,
    string CorrelationId,
    string? UsedMode,
    string? SyncMode,
    int ItemsCount,
    int InsertedNew,
    string? Error,
    long ElapsedMs,
    int PosterRequested,
    int PosterEnqueued,
    int PosterCoalesced,
    int PosterTimedOut);
