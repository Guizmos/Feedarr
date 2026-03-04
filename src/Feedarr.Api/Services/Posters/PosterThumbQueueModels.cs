namespace Feedarr.Api.Services.Posters;

public enum PosterThumbJobReason
{
    MissingThumb = 0,
    Warmup = 1,
    Backfill = 2,
}

public sealed record PosterThumbJob(
    string StoreDir,
    IReadOnlyList<int>? Widths,
    PosterThumbJobReason Reason,
    long? ReleaseId = null);

public enum PosterThumbEnqueueStatus
{
    Enqueued = 0,
    Coalesced = 1,
    TimedOut = 2,
    Rejected = 3,
}

public readonly record struct PosterThumbEnqueueResult(PosterThumbEnqueueStatus Status)
{
    public bool IsEnqueued => Status == PosterThumbEnqueueStatus.Enqueued;
    public bool IsCoalesced => Status == PosterThumbEnqueueStatus.Coalesced;
    public bool IsTimedOut => Status == PosterThumbEnqueueStatus.TimedOut;
    public bool IsRejected => Status == PosterThumbEnqueueStatus.Rejected;
}

internal sealed record PosterThumbWorkResult(
    bool Succeeded,
    bool Skipped,
    string Reason,
    IReadOnlyList<int> GeneratedWidths);
