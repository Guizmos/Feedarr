using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Services.Posters;

public static class PosterProviderSelector
{
    public static float GetTmdbStrongThreshold(TitleAmbiguityResult ambiguity)
        => ambiguity.IsAmbiguous ? 0.65f : 0.50f;
}
