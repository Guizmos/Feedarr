using Feedarr.Api.Models;

namespace Feedarr.Api.Services.Posters;

public interface IMatchingStrategy
{
    Task<PosterFetchResult> FetchPosterAsync(
        PosterFetchService core,
        PosterFetchRoutingContext context,
        CancellationToken ct);
}
