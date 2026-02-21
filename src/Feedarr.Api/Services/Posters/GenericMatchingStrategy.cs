namespace Feedarr.Api.Services.Posters;

public sealed class GenericMatchingStrategy : IMatchingStrategy
{
    public Task<PosterFetchResult> FetchPosterAsync(
        PosterFetchService core,
        PosterFetchRoutingContext context,
        CancellationToken ct)
    {
        return core.FetchGenericBranchAsync(context, ct);
    }
}
