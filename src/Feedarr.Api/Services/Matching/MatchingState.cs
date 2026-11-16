using Feedarr.Api.Models;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Matching;

internal sealed record MatchingShadowState
{
    public long Id { get; init; }
    public long? EntityId { get; init; }
    public string? Revision { get; init; }
    public string? RawTitle { get; init; }
    public string? TitleClean { get; init; }
    public int? Year { get; init; }
    public string? Category { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public int? CategoryId { get; init; }
    public int? StdCategoryId { get; init; }
    public int? SpecCategoryId { get; init; }
    public string? CategoryIds { get; init; }
    public string? MediaType { get; init; }
    public int? TmdbId { get; init; }
    public string? TmdbType { get; init; }
    public int? TvdbId { get; init; }
    public string? ExternalProvider { get; init; }
    public string? ExternalId { get; init; }
    public string? ManualProvider { get; init; }
    public bool ManualTitle { get; init; }
    public bool LegacyManualPoster { get; init; }
    internal bool Protected => ManualProvider is not null || ManualTitle || LegacyManualPoster ||
        TmdbId is > 0 || TvdbId is > 0 || !string.IsNullOrWhiteSpace(ExternalId);

    internal static MatchingShadowState From(ReleaseForPoster s) => new()
    {
        Id = s.Id, EntityId = s.EntityId, Revision = s.MatchRevision, RawTitle = s.Title,
        TitleClean = s.TitleClean, Year = s.Year, Category = s.UnifiedCategory, Season = s.Season, Episode = s.Episode,
        CategoryId = s.CategoryId, StdCategoryId = s.StdCategoryId, SpecCategoryId = s.SpecCategoryId,
        CategoryIds = s.CategoryIds, MediaType = s.MediaType,
        TmdbId = s.TmdbId, TmdbType = s.TmdbMediaType, TvdbId = s.TvdbId, ExternalProvider = s.ExtProvider,
        ExternalId = s.ExtProviderId, ManualProvider = s.ManualMatchProvider,
        LegacyManualPoster = s.MatchRevision is null && s.PosterFile?.Contains("-manual", StringComparison.OrdinalIgnoreCase) == true
    };
}

internal sealed record HistoricalShadowDecision(string Decision, SearchIdentity? Identity, double? Confidence);
internal sealed record MatchingShadowObservation(long ReleaseId, string Status,
    HistoricalShadowDecision Historical, CandidateRankingResult? V2, IReadOnlyList<string> MissingInformation,
    bool PreserveExisting)
{
    internal IReadOnlyList<CollectedSearchPage> CollectedPages { get; init; } = Array.Empty<CollectedSearchPage>();
    internal CandidateRankingContext? RankingContext { get; init; }
    internal MatchingShadowState? InputState { get; init; }
    internal IReadOnlyList<ProviderSearchCandidate> ProviderCandidates { get; init; } = Array.Empty<ProviderSearchCandidate>();
    internal IReadOnlyList<SearchCandidate> Queries { get; init; } = Array.Empty<SearchCandidate>();
}

internal sealed record CollectedSearchPage(string Provider, string Query, string? Language, int Page, int TotalPages, int CandidateCount);
