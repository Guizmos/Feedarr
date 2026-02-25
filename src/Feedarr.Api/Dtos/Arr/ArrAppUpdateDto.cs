using System.ComponentModel.DataAnnotations;

namespace Feedarr.Api.Dtos.Arr;

public sealed class ArrAppUpdateDto
{
    [StringLength(100)]
    public string? Name { get; set; }

    [StringLength(500, MinimumLength = 1)]
    public string? BaseUrl { get; set; }

    [StringLength(200)]
    public string? ApiKey { get; set; }  // null = don't change

    // Default settings
    [StringLength(500)]
    public string? RootFolderPath { get; set; }

    [Range(1, int.MaxValue)]
    public int? QualityProfileId { get; set; }

    public List<int>? Tags { get; set; }

    // Sonarr-specific
    [StringLength(50)]
    public string? SeriesType { get; set; }

    public bool? SeasonFolder { get; set; }

    [StringLength(50)]
    public string? MonitorMode { get; set; }

    public bool? SearchMissing { get; set; }
    public bool? SearchCutoff { get; set; }

    // Radarr-specific
    [StringLength(50)]
    public string? MinimumAvailability { get; set; }

    public bool? SearchForMovie { get; set; }
}
