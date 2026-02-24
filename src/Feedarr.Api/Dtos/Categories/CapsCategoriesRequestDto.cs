namespace Feedarr.Api.Dtos.Categories;

public sealed class CapsCategoriesRequestDto
{
    public long? SourceId { get; set; }
    public string? TorznabUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? AuthMode { get; set; }
    public string? IndexerName { get; set; }

    public bool IncludeStandardCatalog { get; set; } = true;

    public bool IncludeSpecific { get; set; } = true;
}
