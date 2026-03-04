namespace Feedarr.Api.Services.Posters;

public interface IPosterThumbQueue
{
    ValueTask<PosterThumbEnqueueResult> EnqueueAsync(PosterThumbJob job, CancellationToken ct, TimeSpan timeout);
    ValueTask<PosterThumbJob> DequeueAsync(CancellationToken ct);
    PosterThumbJob? Complete(PosterThumbJob job);
    int Count { get; }
}

internal sealed class NoOpPosterThumbQueue : IPosterThumbQueue
{
    public static readonly NoOpPosterThumbQueue Instance = new();

    private NoOpPosterThumbQueue()
    {
    }

    public ValueTask<PosterThumbEnqueueResult> EnqueueAsync(PosterThumbJob job, CancellationToken ct, TimeSpan timeout)
        => ValueTask.FromResult(new PosterThumbEnqueueResult(PosterThumbEnqueueStatus.Rejected));

    public ValueTask<PosterThumbJob> DequeueAsync(CancellationToken ct)
        => ValueTask.FromCanceled<PosterThumbJob>(ct);

    public PosterThumbJob? Complete(PosterThumbJob job)
        => null;

    public int Count => 0;
}
