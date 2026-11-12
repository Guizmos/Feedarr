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
        TmdbId = s.TmdbId, TmdbType = s.TmdbMediaType, TvdbId = s.TvdbId, ExternalProvider = s.ExtProvider,
        ExternalId = s.ExtProviderId, ManualProvider = s.ManualMatchProvider,
        LegacyManualPoster = s.MatchRevision is null && s.PosterFile?.Contains("-manual", StringComparison.OrdinalIgnoreCase) == true
    };
}

internal sealed record HistoricalShadowDecision(string Decision, SearchIdentity? Identity, double? Confidence);
internal sealed record MatchingShadowBatch(string QueryTitle, int? QueryYear,
    IReadOnlyList<TmdbClient.SearchResult>? Results, bool CoverageComplete);
internal sealed record MatchingShadowObservation(long ReleaseId, string Status,
    HistoricalShadowDecision Historical, CandidateRankingResult? V2, IReadOnlyList<string> MissingInformation,
    bool PreserveExisting)
{
    internal IReadOnlyList<SearchCandidate> Queries { get; init; } = Array.Empty<SearchCandidate>();
}

/// <summary>No provider, repository, logger or writer dependency. Its only callback can read a snapshot.</summary>
internal static class MatchingShadowEvaluator
{
    internal static MatchingShadowObservation? Observe(bool enabled, MatchingShadowState expected,
        Func<MatchingShadowState?> readCurrent, MatchingShadowBatch? batch, HistoricalShadowDecision historical)
    {
        if (!enabled) return null;
        IReadOnlyList<SearchCandidate> queries = Array.Empty<SearchCandidate>();
        MatchingShadowObservation Missing(string reason, bool preserve = false) =>
            new(expected.Id, "not_evaluable", historical, null, Array.AsReadOnly(new[] { reason }), preserve) { Queries = queries };
        try
        {
            var current = readCurrent();
            if (current is null) return Missing("persisted_state_unavailable");
            if (!SameIdentityAndInput(expected, current)) return Missing("persisted_state_changed", true);
            if (current.Protected)
                return Missing(current.TmdbId is > 0 && TmdbIdentity.NormalizeType(current.TmdbType) is null
                    ? "established_tmdb_type_unknown" : "persisted_identity_or_manual_correction_protected", true);
            if (batch?.Results is null) return Missing("provider_response_unavailable_or_cache_only");
            if (!batch.CoverageComplete) return Missing("provider_response_partial_or_coverage_unknown");
            if (!UnifiedCategoryMappings.TryParse(current.Category, out var category)) return Missing("category_unknown");
            var type = TmdbIdentity.NormalizeType(UnifiedCategoryMappings.ToMediaType(category));
            if (type is null) return Missing("media_type_unsupported");
            queries = SearchCandidateGenerator.Generate(new(current.RawTitle, category));
            if (queries.Count == 0) return Missing("no_query_generated");
            if (queries.Any(q => TitleNormalizer.NormalizeTitleStrict(q.Title) != TitleNormalizer.NormalizeTitleStrict(batch.QueryTitle) ||
                (q.Year.HasValue && q.Year != batch.QueryYear)))
                return Missing("generated_query_not_covered_by_historical_requests");
            if (batch.Results.Count > new CandidateRankingPolicy().MaximumResults) return Missing("provider_result_limit_exceeded");
            if (batch.Results.Any(r => r.TmdbId <= 0 || TmdbIdentity.NormalizeType(r.MediaType) is null))
                return Missing("provider_identity_type_or_id_missing");
            var results = batch.Results.Select(r => new ProviderSearchCandidate(
                new("tmdb", TmdbIdentity.NormalizeType(r.MediaType) == "movie" ? SearchMediaType.Movie : SearchMediaType.Series, r.TmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                r.Title, r.Year, string.IsNullOrWhiteSpace(r.OriginalTitle) ? null : new[] { r.OriginalTitle })).ToArray();
            var ranked = SearchCandidateRanker.Rank(new(queries, category, type == "movie" ? SearchMediaType.Movie : SearchMediaType.Series,
                Season: current.Season, Episode: current.Episode), results);
            // Re-read after evaluation as well: a worker result computed before a
            // concurrent manual correction is never published as a current shadow decision.
            if (readCurrent() is not { } latest || latest != current) return Missing("persisted_state_changed_during_evaluation", true);
            return new(expected.Id, "evaluated", historical, ranked, Array.Empty<string>(), false) { Queries = queries };
        }
        catch (Exception)
        {
            // Shadow failure must not escape into historical selection or persistence.
            return Missing("shadow_evaluation_failed");
        }
    }

    private static bool SameIdentityAndInput(MatchingShadowState a, MatchingShadowState b)
        => a with { ManualTitle = b.ManualTitle } == b;
}
