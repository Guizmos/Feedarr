namespace Feedarr.Api.Options;

public sealed class AppOptions
{
    // Opt-in rollout watermark: only newly ingested, unassociated movies may use V2.
    public bool MatchingV2MoviesEnabled { get; set; } = false;
    public long MatchingV2MoviesMinReleaseId { get; set; } = long.MaxValue;
    public int MatchingV2MoviesSearchBudget { get; set; } = 12;
    public int MatchingV2MoviesMaxPagesPerQuery { get; set; } = 3;
    internal bool ValidMatchingV2Rollout => !MatchingV2MoviesEnabled ||
        (MatchingV2MoviesMinReleaseId is > 0 and < long.MaxValue &&
         MatchingV2MoviesSearchBudget is >= 1 and <= 12 && MatchingV2MoviesMaxPagesPerQuery is >= 1 and <= 3);
    public string DataDir { get; set; } = "data";
    public string DbFileName { get; set; } = "feedarr.db";
    public int SyncIntervalMinutes { get; set; } = 60;
    public int SyncSourcesMaxConcurrency { get; set; } = 2;
    public int RssLimit { get; set; } = 100;
    public int RssLimitPerCategory { get; set; } = 50;
    public int RssLimitGlobalPerSource { get; set; } = 250;
    public bool RssOnlySync { get; set; } = false;
    public int StorageUsageCacheTtlSeconds { get; set; } = 30;
    public int SystemStatusCacheSeconds { get; set; } = 7;
    public int PosterStatsRefreshSeconds { get; set; } = 60;
    public int BadgesSseCoalesceMs { get; set; } = 750;
    public int BadgesSummaryCacheSeconds { get; set; } = 3;
    public int ThumbEnqueueTimeoutMs { get; set; } = 2000;
    public int ThumbWorkers { get; set; } = 1;
    public int MissingPosterSweepMinutes { get; set; } = 10;
    public int MissingPosterSweepBatchSize { get; set; } = 200;
    public int MissingPosterSweepShortCooldownMinutes { get; set; } = 15;
    public int MissingPosterSweepHardFailCooldownMinutes { get; set; } = 1440;
}
