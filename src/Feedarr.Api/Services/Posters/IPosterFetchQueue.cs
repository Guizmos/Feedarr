namespace Feedarr.Api.Services.Posters;

public interface IPosterFetchQueue
{
    bool Enqueue(PosterFetchJob job);
    ValueTask<PosterFetchJob> DequeueAsync(CancellationToken ct);
    int ClearPending();
    int Count { get; }
}
