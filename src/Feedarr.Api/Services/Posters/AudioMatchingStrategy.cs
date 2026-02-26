namespace Feedarr.Api.Services.Posters;

public sealed class AudioMatchingStrategy : IMatchingStrategy
{
    public Task<PosterFetchResult> FetchPosterAsync(
        PosterFetchService core,
        PosterFetchRoutingContext context,
        CancellationToken ct)
    {
        return core.FetchAudioBranchAsync(context, ct);
    }
}
