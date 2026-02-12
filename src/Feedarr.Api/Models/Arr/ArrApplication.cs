namespace Feedarr.Api.Models.Arr;

public sealed class ArrApplication
{
    public long Id { get; set; }
    public string Type { get; set; } = "";  // sonarr | radarr
    public string? Name { get; set; }
    public string BaseUrl { get; set; } = "";
    public string ApiKeyEncrypted { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public bool IsDefault { get; set; }

    // Default settings
    public string? RootFolderPath { get; set; }
    public int? QualityProfileId { get; set; }
    public string? Tags { get; set; }  // JSON array

    // Sonarr-specific
    public string? SeriesType { get; set; }  // standard, daily, anime
    public bool SeasonFolder { get; set; } = true;
    public string? MonitorMode { get; set; }  // all, future, missing, existing, firstSeason, lastSeason, pilot, none
    public bool SearchMissing { get; set; } = true;
    public bool SearchCutoff { get; set; }

    // Radarr-specific
    public string? MinimumAvailability { get; set; }  // announced, inCinemas, released
    public bool SearchForMovie { get; set; } = true;

    public long CreatedAtTs { get; set; }
    public long UpdatedAtTs { get; set; }
}
