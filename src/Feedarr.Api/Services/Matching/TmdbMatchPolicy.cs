using Feedarr.Api.Models;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Matching;

public static class TmdbMatchPolicy
{
    public static bool IsAcceptable(
        string title,
        int? year,
        UnifiedCategory category,
        TitleAmbiguityResult ambiguity,
        TmdbClient.SearchResult candidate,
        float score,
        bool allowWeak,
        float strongThreshold,
        float commonTitleThreshold)
    {
        if (candidate is null || candidate.TmdbId <= 0) return false;

        if (ambiguity.IsCommonTitle)
        {
            if (!year.HasValue || !candidate.Year.HasValue || candidate.Year != year)
                return false;
            if (score < commonTitleThreshold)
                return false;
        }

        if (ambiguity.IsAmbiguous)
        {
            if (score < strongThreshold) return false;
            if (!year.HasValue)
            {
                var overlap = TitleTokenHelper.CountSignificantTokenOverlap(title, candidate.Title, candidate.OriginalTitle);
                if (overlap < 2 && score < 0.85f)
                    return false;
            }
        }

        if (score >= strongThreshold) return true;

        if (allowWeak && IsWeakTmdbMatchAcceptable(title, year, category, candidate, score))
            return true;

        return false;
    }

    private static bool IsWeakTmdbMatchAcceptable(
        string title,
        int? year,
        UnifiedCategory category,
        TmdbClient.SearchResult candidate,
        float score)
    {
        if (score < 0.25f) return false;
        if (!year.HasValue || !candidate.Year.HasValue || candidate.Year != year) return false;
        var expectedMediaType = Feedarr.Api.Services.Categories.UnifiedCategoryMappings.ToMediaType(category);
        if (!string.IsNullOrWhiteSpace(expectedMediaType) &&
            expectedMediaType != "unknown" &&
            !string.Equals(expectedMediaType, candidate.MediaType, StringComparison.OrdinalIgnoreCase))
            return false;

        var overlap = TitleTokenHelper.CountSignificantTokenOverlap(title, candidate.Title, candidate.OriginalTitle);
        return overlap > 0;
    }
}
