namespace Feedarr.Api.Models.Settings;

public sealed class GeneralSettings
{
    // RSS/Indexer sync settings
    public int SyncIntervalMinutes { get; set; } = 60; // 1..1440
    public int RssLimit { get; set; } = 100;           // legacy
    public int RssLimitPerCategory { get; set; } = 50; // 1..200
    public int RssLimitGlobalPerSource { get; set; } = 250; // 1..2000
    public bool AutoSyncEnabled { get; set; } = true;

    // Arr applications (Sonarr/Radarr) sync settings
    public int ArrSyncIntervalMinutes { get; set; } = 60; // 1..1440
    public bool ArrAutoSyncEnabled { get; set; } = true;

    // Request integration mode for release actions in UI
    // arr: direct Sonarr/Radarr
    // overseerr: create request via Overseerr
    // jellyseerr: create request via Jellyseerr
    // seer: create request via Seer
    public string RequestIntegrationMode { get; set; } = "arr";
}
