using Feedarr.Api.Models;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Matching;

public enum TmdbYearRelation { Unknown, Exact, AdjacentRelease, EpisodeAfterPremiere, Contradictory }
public sealed record TmdbMatchDecision(bool Accepted, string Reason, TmdbYearRelation YearRelation);

public static class TmdbMatchPolicy
{
    public static bool IsAcceptable(string title, int? year, UnifiedCategory category,
        TitleAmbiguityResult ambiguity, TmdbClient.SearchResult candidate, float score,
        bool allowWeak, float strongThreshold, float commonTitleThreshold, int? season = null, int? episode = null)
        => Evaluate(title, year, category, ambiguity, candidate, score, allowWeak,
            strongThreshold, commonTitleThreshold, season, episode).Accepted;

    public static TmdbMatchDecision Evaluate(string title, int? year, UnifiedCategory category,
        TitleAmbiguityResult ambiguity, TmdbClient.SearchResult candidate, float score,
        bool allowWeak, float strongThreshold, float commonTitleThreshold, int? season = null, int? episode = null)
    {
        var relation = TmdbYearRelation.Unknown;
        TmdbMatchDecision Reject(string reason) => new(false, reason, relation);
        if (candidate is null || candidate.TmdbId <= 0) return Reject("invalid_candidate");
        var expectedType = TmdbIdentity.NormalizeType(UnifiedCategoryMappings.ToMediaType(category));
        var candidateType = TmdbIdentity.NormalizeType(candidate.MediaType);
        if (expectedType is not null && candidateType is not null && expectedType != candidateType)
            return Reject("media_type_conflict");

        var exactTitle = TitleNormalizer.NormalizeTitle(title) is { Length: > 0 } normalized &&
            (normalized == TitleNormalizer.NormalizeTitle(candidate.Title) ||
             normalized == TitleNormalizer.NormalizeTitle(candidate.OriginalTitle));
        if (year is > 0 && candidate.Year is > 0)
        {
            var delta = (long)year.Value - candidate.Year.Value;
            relation = delta == 0 ? TmdbYearRelation.Exact
                : Math.Abs(delta) == 1 ? TmdbYearRelation.AdjacentRelease
                // An episode's year is not necessarily the show's premiere year.
                : expectedType == "tv" && (season is > 1 or 0 || (season is null && episode.HasValue)) && delta > 1 && exactTitle && !ambiguity.IsCommonTitle
                    ? TmdbYearRelation.EpisodeAfterPremiere : TmdbYearRelation.Contradictory;
            if (relation == TmdbYearRelation.Contradictory) return Reject("release_year_conflict");
            if (relation == TmdbYearRelation.AdjacentRelease && !exactTitle &&
                (ambiguity.IsAmbiguous || score < Math.Max(strongThreshold, 0.80f)))
                return Reject("adjacent_year_requires_title_support");
        }
        if (ambiguity.IsCommonTitle)
        {
            if (relation != TmdbYearRelation.Exact) return Reject("common_title_requires_exact_year");
            if (score < commonTitleThreshold) return Reject("common_title_score");
        }
        if (ambiguity.IsAmbiguous)
        {
            if (score < strongThreshold) return Reject("ambiguous_title_score");
            if ((year is null or <= 0) && TitleTokenHelper.CountSignificantTokenOverlap(title, candidate.Title, candidate.OriginalTitle) < 2 && score < 0.85f)
                return Reject("ambiguous_title_without_support");
        }
        if (score >= strongThreshold) return new(true, "accepted_strong", relation);
        if (allowWeak && relation == TmdbYearRelation.Exact && score >= 0.25f &&
            TitleTokenHelper.CountSignificantTokenOverlap(title, candidate.Title, candidate.OriginalTitle) > 0)
            return new(true, "accepted_weak_exact_year", relation);
        return Reject("insufficient_score");
    }
}
