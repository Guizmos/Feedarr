using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Services.Posters;

public sealed record PosterFetchRoutingContext(
    long ReleaseId,
    string Title,
    int? Year,
    string CategoryName,
    UnifiedCategory UnifiedCategory,
    string MediaType,
    int? TmdbIdStored,
    int? TvdbIdStored,
    string NormalizedTitle,
    int? Season,
    int? Episode,
    TitleAmbiguityResult Ambiguity,
    PosterTitleKey TitleKey,
    string Fingerprint,
    PosterMatchIds KnownIds,
    bool TvmazeEnabled,
    bool LogSingle,
    long? SourceId);
