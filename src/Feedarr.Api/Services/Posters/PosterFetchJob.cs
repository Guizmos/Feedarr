using Feedarr.Api.Models;

namespace Feedarr.Api.Services.Posters;

public sealed record PosterFetchJob(
    long ItemId,
    string Title,
    int? Year,
    UnifiedCategory Category,
    bool ForceRefresh,
    int AttemptCount,
    long? EntityId,
    string? RetroLogFile);
