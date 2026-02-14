namespace Feedarr.Api.Dtos.Arr;

public sealed class ArrRequestAddRequestDto
{
    public string AppType { get; set; } = ""; // overseerr | jellyseerr
    public long? AppId { get; set; } // optional specific app
    public long? ReleaseId { get; set; } // optional release context for id resolution/persistence
    public int? TmdbId { get; set; }
    public int? TvdbId { get; set; }
    public string? MediaType { get; set; } // movie | series | tv
    public string? Title { get; set; }
    public int? Year { get; set; }
}
