namespace Feedarr.Api.Services.Posters;

public sealed class VideoMatchingStrategy : IMatchingStrategy
{
    public Task<PosterFetchResult> FetchPosterAsync(
        PosterFetchService core,
        PosterFetchRoutingContext context,
        CancellationToken ct)
    {
        return core.FetchVideoBranchAsync(context, ct);
    }
}
