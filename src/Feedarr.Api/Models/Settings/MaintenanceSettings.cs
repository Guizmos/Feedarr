namespace Feedarr.Api.Models.Settings;

public sealed class MaintenanceSettings
{
    public bool MaintenanceAdvancedOptionsEnabled { get; set; } = false;
    public int SyncSourcesMaxConcurrency { get; set; } = 2;
    public int PosterWorkers { get; set; } = 1;
    public string ProviderRateLimitMode { get; set; } = "auto";
    public ProviderConcurrencyManualSettings ProviderConcurrencyManual { get; set; } = new();
    public int SyncRunTimeoutMinutes { get; set; } = 10;
}

public sealed class MaintenanceSettingsResponse
{
    public bool MaintenanceAdvancedOptionsEnabled { get; set; } = false;
    public int SyncSourcesMaxConcurrency { get; set; } = 2;
    public int PosterWorkers { get; set; } = 1;
    public string ProviderRateLimitMode { get; set; } = "auto";
    public ProviderConcurrencyManualSettings ProviderConcurrencyManual { get; set; } = new();
    public int SyncRunTimeoutMinutes { get; set; } = 10;
    public IReadOnlyList<string> ConfiguredProviders { get; set; } = [];
}

public sealed class ProviderConcurrencyManualSettings
{
    public int Tmdb { get; set; } = 2;
    public int Igdb { get; set; } = 1;
    public int Fanart { get; set; } = 1;
    public int Tvmaze { get; set; } = 1;
    public int Others { get; set; } = 1;
}
