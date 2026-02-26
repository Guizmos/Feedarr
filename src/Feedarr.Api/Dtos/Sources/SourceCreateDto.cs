using System.ComponentModel.DataAnnotations;

namespace Feedarr.Api.Dtos.Sources;

public sealed class SourceCreateDto
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = "";

    [Required]
    [StringLength(1000, MinimumLength = 1)]
    public string TorznabUrl { get; set; } = "";

    [StringLength(500)]
    public string? ApiKey { get; set; }

    [Required]
    [StringLength(10)]
    public string AuthMode { get; set; } = "query";

    public List<SourceCategorySelectionDto>? Categories { get; set; }

    [Range(1, long.MaxValue)]
    public long? ProviderId { get; set; }

    [StringLength(20)]
    public string? Color { get; set; }
}
