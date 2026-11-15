using Feedarr.Api.Models;

namespace Feedarr.Api.Services.Posters;

public sealed record PosterFetchRoutingContext(
    long ReleaseId, UnifiedCategory UnifiedCategory, bool TvmazeEnabled, bool ForceRefresh, long? SourceId)
{
    internal string? VideoType { get; init; }
}
