namespace Feedarr.Api.Dtos.Sources;

public sealed class SourceCategoryMappingsPatchDto
{
    public List<SourceCategoryMappingPatchItemDto> Mappings { get; set; } = new();
}

public sealed class SourceCategoryMappingPatchItemDto
{
    public int CatId { get; set; }
    public string? GroupKey { get; set; }
}
