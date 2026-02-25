using System.ComponentModel.DataAnnotations;

namespace Feedarr.Api.Dtos.Sources;

public sealed class SourceTestRequestDto
{
    [Required]
    [StringLength(1000, MinimumLength = 1)]
    public string TorznabUrl { get; set; } = "";

    [StringLength(500)]
    public string ApiKey { get; set; } = "";

    [Required]
    [StringLength(10)]
    public string AuthMode { get; set; } = "query"; // query|header

    [Range(1, 500)]
    public int RssLimit { get; set; } = 50;
}
