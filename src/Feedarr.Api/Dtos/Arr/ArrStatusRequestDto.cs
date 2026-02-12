namespace Feedarr.Api.Dtos.Arr;

public sealed class ArrStatusRequestDto
{
    public List<ArrStatusItemDto> Items { get; set; } = new();
}

public sealed class ArrStatusItemDto
{
    public long? ReleaseId { get; set; }
    public int? TvdbId { get; set; }
    public int? TmdbId { get; set; }
    public string? MediaType { get; set; }  // series | movie
    public string? Title { get; set; }  // For fallback matching when IDs are not available
}

public sealed class ArrStatusResponseDto
{
    public List<ArrStatusResultDto> Results { get; set; } = new();
}

public sealed class ArrStatusResultDto
{
    public int? TvdbId { get; set; }
    public int? TmdbId { get; set; }
    public bool Exists { get; set; }
    public bool InSonarr { get; set; }
    public bool InRadarr { get; set; }
    public int? SonarrSeriesId { get; set; }
    public int? RadarrMovieId { get; set; }
    public string? SonarrUrl { get; set; }
    public string? RadarrUrl { get; set; }
    // Legacy fields for backwards compatibility
    public int? ExternalId { get; set; }
    public string? OpenUrl { get; set; }
}
