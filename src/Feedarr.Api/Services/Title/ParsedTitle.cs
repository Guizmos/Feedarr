namespace Feedarr.Api.Services.Titles;

public sealed class ParsedTitle
{
    public string TitleClean { get; set; } = "";
    public int? Year { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public string? AirDate { get; set; }
    public string? Resolution { get; set; }
    public string? Source { get; set; }
    public string? Codec { get; set; }
    public string? ReleaseGroup { get; set; }
    public string MediaType { get; set; } = "unknown";
}
