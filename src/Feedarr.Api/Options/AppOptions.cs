namespace Feedarr.Api.Options;

public sealed class AppOptions
{
    public string DataDir { get; set; } = "data";
    public string DbFileName { get; set; } = "feedarr.db";
    public int SyncIntervalMinutes { get; set; } = 60;
    public int RssLimit { get; set; } = 100;
    public int RssLimitPerCategory { get; set; } = 50;
    public int RssLimitGlobalPerSource { get; set; } = 250;
    public bool RssOnlySync { get; set; } = false;
    public int StorageUsageCacheTtlSeconds { get; set; } = 30;
}
