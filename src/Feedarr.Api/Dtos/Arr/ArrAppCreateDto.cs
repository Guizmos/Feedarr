namespace Feedarr.Api.Dtos.Arr;

public sealed class ArrAppCreateDto
{
    public string Type { get; set; } = "";  // sonarr | radarr | overseerr | jellyseerr | seer
    public string? Name { get; set; }
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";

    // Default settings
    public string? RootFolderPath { get; set; }
    public int? QualityProfileId { get; set; }
    public List<int>? Tags { get; set; }

    // Sonarr-specific
    public string? SeriesType { get; set; }
    public bool? SeasonFolder { get; set; }
    public string? MonitorMode { get; set; }
    public bool? SearchMissing { get; set; }
    public bool? SearchCutoff { get; set; }

    // Radarr-specific
    public string? MinimumAvailability { get; set; }
    public bool? SearchForMovie { get; set; }
}
