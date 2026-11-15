using Feedarr.Api.Models;

namespace Feedarr.Api.Services.Posters;

public sealed class PosterMatchingOrchestrator
{
    private readonly VideoMatchingStrategy _videoMatchingStrategy;
    private readonly GameMatchingStrategy _gameMatchingStrategy;
    private readonly AnimeMatchingStrategy _animeMatchingStrategy;
    private readonly AudioMatchingStrategy _audioMatchingStrategy;
    private readonly GenericMatchingStrategy _genericMatchingStrategy;

    public PosterMatchingOrchestrator(
        VideoMatchingStrategy videoMatchingStrategy,
        GameMatchingStrategy gameMatchingStrategy,
        AnimeMatchingStrategy animeMatchingStrategy,
        AudioMatchingStrategy audioMatchingStrategy,
        GenericMatchingStrategy genericMatchingStrategy)
    {
        _videoMatchingStrategy = videoMatchingStrategy;
        _gameMatchingStrategy = gameMatchingStrategy;
        _animeMatchingStrategy = animeMatchingStrategy;
        _audioMatchingStrategy = audioMatchingStrategy;
        _genericMatchingStrategy = genericMatchingStrategy;
    }

    public Task<PosterFetchResult> FetchPosterAsync(
        PosterFetchService core,
        PosterFetchRoutingContext context,
        CancellationToken ct)
    {
        if (context.VideoType is "movie" or "tv")
            return core.FetchVideoV2Async(context, ct);
        return context.UnifiedCategory switch
        {
            UnifiedCategory.JeuWindows => _gameMatchingStrategy.FetchPosterAsync(core, context, ct),
            UnifiedCategory.Anime => _animeMatchingStrategy.FetchPosterAsync(core, context, ct),
            UnifiedCategory.Audio => _audioMatchingStrategy.FetchPosterAsync(core, context, ct),
            UnifiedCategory.Book => _genericMatchingStrategy.FetchPosterAsync(core, context, ct),
            UnifiedCategory.Comic => _genericMatchingStrategy.FetchPosterAsync(core, context, ct),
            UnifiedCategory.Film => _videoMatchingStrategy.FetchPosterAsync(core, context, ct),
            UnifiedCategory.Spectacle => _videoMatchingStrategy.FetchPosterAsync(core, context, ct),
            UnifiedCategory.Animation => _videoMatchingStrategy.FetchPosterAsync(core, context, ct),
            _ => Task.FromResult(new PosterFetchResult(false, 422, new { error = "matching_v2_abstained", reason = "video_type_unproven_or_conflicting" }, context.SourceId))
        };
    }
}
