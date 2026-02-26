namespace Feedarr.Api.Dtos.Sources;

public sealed class SourceCategoryMappingDto
{
    public int CatId { get; set; }
    public string GroupKey { get; set; } = "";
    public string GroupLabel { get; set; } = "";
}
