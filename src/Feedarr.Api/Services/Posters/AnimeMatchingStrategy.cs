namespace Feedarr.Api.Services.Posters;

public sealed class AnimeMatchingStrategy : IMatchingStrategy
{
    public Task<PosterFetchResult> FetchPosterAsync(
        PosterFetchService core,
        PosterFetchRoutingContext context,
        CancellationToken ct)
    {
        return core.FetchAnimeBranchAsync(context, ct);
    }
}
