namespace Feedarr.Api.Options;

/// <summary>
/// Configures the bounded in-process cache used by ArrLibraryCacheService
/// for Sonarr/Radarr title lookups.
/// </summary>
public sealed class ArrLibraryCacheOptions
{
    /// <summary>
    /// Maximum number of title-key entries across the Sonarr (or Radarr) title cache.
    /// Each series/movie can produce several title variants (main + alternates),
    /// so the effective series count is roughly MaxTitleEntries / avgVariantsPerSeries.
    /// Default: 5 000.
    /// </summary>
    public int MaxTitleEntries { get; set; } = 5_000;

    /// <summary>
    /// Sliding expiration for title cache entries, in minutes.
    /// The TTL resets on each successful lookup. Default: 30.
    /// </summary>
    public int SlidingExpirationMinutes { get; set; } = 30;

    /// <summary>
    /// Absolute expiration for title cache entries, in hours.
    /// Guarantees periodic refresh even for frequently-accessed entries. Default: 6.
    /// </summary>
    public int AbsoluteExpirationHours { get; set; } = 6;
}
