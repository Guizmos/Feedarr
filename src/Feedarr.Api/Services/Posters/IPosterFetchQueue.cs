namespace Feedarr.Api.Services.Posters;

public interface IPosterFetchQueue
{
    /// <summary>
    /// Attempts to enqueue a poster fetch job.
    /// Returns <c>true</c> if the job will be processed (newly queued, or already pending).
    /// Returns <c>false</c> if the channel is full and the job was dropped; the item
    /// will be retried on the next sync cycle.
    /// </summary>
    bool TryEnqueue(PosterFetchJob job);
    ValueTask<PosterFetchJob> DequeueAsync(CancellationToken ct);
    int ClearPending();
    int Count { get; }
}
