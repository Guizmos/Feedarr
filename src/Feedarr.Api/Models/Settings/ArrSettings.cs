namespace Feedarr.Api.Models.Settings;

public sealed class ArrSettings
{
    public int ArrSyncIntervalMinutes { get; set; } = 60;
    public bool ArrAutoSyncEnabled { get; set; } = true;
    public string RequestIntegrationMode { get; set; } = "arr";

    public static ArrSettings BuildDefaults() => new();
}
