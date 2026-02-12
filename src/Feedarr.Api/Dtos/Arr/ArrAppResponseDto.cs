namespace Feedarr.Api.Dtos.Arr;

public sealed class ArrAppResponseDto
{
    public long Id { get; set; }
    public string Type { get; set; } = "";
    public string? Name { get; set; }
    public string BaseUrl { get; set; } = "";
    public bool HasApiKey { get; set; }  // Never expose the actual key
    public bool IsEnabled { get; set; }
    public bool IsDefault { get; set; }

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
