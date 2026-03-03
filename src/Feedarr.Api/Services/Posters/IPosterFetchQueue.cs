namespace Feedarr.Api.Services.Posters;

public interface IPosterFetchQueue
{
    ValueTask<PosterFetchEnqueueResult> EnqueueAsync(PosterFetchJob job, CancellationToken ct, TimeSpan timeout);
    ValueTask<PosterFetchJob> DequeueAsync(CancellationToken ct);
    void RecordRetry();
    PosterFetchJob? Complete(PosterFetchJob job, PosterFetchProcessResult result);
    int ClearPending();
    int Count { get; }
    PosterFetchQueueSnapshot GetSnapshot();
}
