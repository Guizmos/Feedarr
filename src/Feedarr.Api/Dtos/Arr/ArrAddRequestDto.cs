namespace Feedarr.Api.Dtos.Arr;

public sealed class SonarrAddRequestDto
{
    public int TvdbId { get; set; }
    public string? Title { get; set; }
    public long? AppId { get; set; }  // If null, use default

    // Optional overrides
    public string? RootFolderPath { get; set; }
    public int? QualityProfileId { get; set; }
    public List<int>? Tags { get; set; }
    public string? SeriesType { get; set; }
    public bool? SeasonFolder { get; set; }
    public string? MonitorMode { get; set; }
    public bool? SearchMissing { get; set; }
    public bool? SearchCutoff { get; set; }
}

public sealed class RadarrAddRequestDto
{
    public int TmdbId { get; set; }
    public string? Title { get; set; }
    public int? Year { get; set; }
    public long? AppId { get; set; }  // If null, use default

    // Optional overrides
    public string? RootFolderPath { get; set; }
    public int? QualityProfileId { get; set; }
    public List<int>? Tags { get; set; }
    public string? MinimumAvailability { get; set; }
    public bool? SearchForMovie { get; set; }
}
