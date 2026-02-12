using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Services.Posters;

public static class PosterProviderSelector
{
    public static bool ShouldAutoMatchEmission(TitleAmbiguityResult ambiguity)
    {
        return !ambiguity.IsAmbiguous
               && !ambiguity.IsLikelyChannelOrProgram
               && ambiguity.SignificantTokenCount >= 2;
    }

    public static bool ShouldUseTvMaze(TitleAmbiguityResult ambiguity, UnifiedCategory category)
    {
        if (category == UnifiedCategory.Emission)
            return ShouldAutoMatchEmission(ambiguity);
        if (category == UnifiedCategory.Serie)
            return !ambiguity.IsLikelyChannelOrProgram && ambiguity.SignificantTokenCount >= 2;
        return false;
    }

    public static IReadOnlyList<string> GetExternalProviderOrder(
        string mediaType,
        UnifiedCategory category,
        TitleAmbiguityResult ambiguity)
    {
        if (string.Equals(mediaType, "series", StringComparison.OrdinalIgnoreCase))
        {
            var list = new List<string>();
            if (ShouldUseTvMaze(ambiguity, category))
                list.Add("tvmaze");
            list.Add("tmdb");
            return list;
        }

        if (string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase))
            return new[] { "tmdb" };

        return Array.Empty<string>();
    }

    public static float GetTvMazeThreshold(TitleAmbiguityResult ambiguity, UnifiedCategory category)
    {
        if (category == UnifiedCategory.Emission) return 0.65f;
        return ambiguity.IsAmbiguous ? 0.65f : 0.55f;
    }

    public static float GetTmdbStrongThreshold(TitleAmbiguityResult ambiguity)
        => ambiguity.IsAmbiguous ? 0.65f : 0.50f;
}
