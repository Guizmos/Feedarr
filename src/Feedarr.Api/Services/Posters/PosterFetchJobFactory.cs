using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Categories;

namespace Feedarr.Api.Services.Posters;

public sealed class PosterFetchJobFactory
{
    private readonly ReleaseRepository _releases;

    public PosterFetchJobFactory(ReleaseRepository releases)
    {
        _releases = releases;
    }

    public PosterFetchJob? Create(long itemId, bool forceRefresh, string? retroLogFile = null)
    {
        var r = _releases.GetForPoster(itemId);
        if (r is null) return null;

        var title = (string?)r.TitleClean ?? (string?)r.Title ?? "";
        var year = r.Year is null ? (int?)null : Convert.ToInt32(r.Year);
        var unifiedValue = (string?)r.UnifiedCategory;
        var entityId = r.EntityId is null ? (long?)null : Convert.ToInt64(r.EntityId);
        UnifiedCategoryMappings.TryParse(unifiedValue, out var unifiedCategory);

        return new PosterFetchJob(itemId, title, year, unifiedCategory, forceRefresh, 0, entityId, retroLogFile);
    }

    public PosterFetchJob? CreateFromSeed(ReleaseRepository.PosterJobSeed seed, bool forceRefresh, string? retroLogFile = null)
    {
        if (seed is null) return null;
        var title = seed.TitleClean ?? seed.Title ?? "";
        var year = seed.Year;
        UnifiedCategoryMappings.TryParse(seed.UnifiedCategory, out var unifiedCategory);
        return new PosterFetchJob(seed.Id, title, year, unifiedCategory, forceRefresh, 0, seed.EntityId, retroLogFile);
    }
}
