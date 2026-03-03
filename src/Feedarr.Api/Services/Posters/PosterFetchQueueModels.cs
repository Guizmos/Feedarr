namespace Feedarr.Api.Services.Posters;

public enum PosterFetchEnqueueStatus
{
    Enqueued = 0,
    Coalesced = 1,
    TimedOut = 2,
    Rejected = 3,
}

public readonly record struct PosterFetchEnqueueResult(PosterFetchEnqueueStatus Status)
{
    public bool IsEnqueued => Status == PosterFetchEnqueueStatus.Enqueued;
    public bool IsCoalesced => Status == PosterFetchEnqueueStatus.Coalesced;
    public bool IsTimedOut => Status == PosterFetchEnqueueStatus.TimedOut;
    public bool IsRejected => Status == PosterFetchEnqueueStatus.Rejected;
}

public readonly record struct PosterFetchProcessResult(bool Succeeded);

public sealed record PosterFetchCurrentJobSnapshot(
    long ItemId,
    bool ForceRefresh,
    long StartedAtTs);

public sealed record PosterFetchQueueSnapshot(
    int PendingCount,
    int InFlightCount,
    bool IsProcessing,
    long? OldestQueuedAgeMs,
    long? LastJobStartedAtTs,
    long? LastJobEndedAtTs,
    PosterFetchCurrentJobSnapshot? CurrentJob,
    long JobsEnqueued,
    long JobsCoalesced,
    long JobsTimedOut,
    long JobsProcessed,
    long JobsSucceeded,
    long JobsFailed,
    long JobsRetried);
