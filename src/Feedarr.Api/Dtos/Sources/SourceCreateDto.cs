namespace Feedarr.Api.Dtos.Sources;

public sealed class SourceCreateDto
{
    public string Name { get; set; } = "";
    public string TorznabUrl { get; set; } = "";
    public string? ApiKey { get; set; }
    public string AuthMode { get; set; } = "query";
    public List<SourceCategorySelectionDto>? Categories { get; set; }
    public long? ProviderId { get; set; }
    public string? Color { get; set; }
}
