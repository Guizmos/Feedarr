namespace Feedarr.Api.Services.Posters;

public sealed class GameMatchingStrategy : IMatchingStrategy
{
    public Task<PosterFetchResult> FetchPosterAsync(
        PosterFetchService core,
        PosterFetchRoutingContext context,
        CancellationToken ct)
    {
        return core.FetchGameBranchAsync(context, ct);
    }
}
