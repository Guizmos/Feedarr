namespace Feedarr.Api.Dtos.Sources;

public sealed class SourceCategorySelectionDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsSub { get; set; }
    public int? ParentId { get; set; }
    public string UnifiedKey { get; set; } = "";
    public string UnifiedLabel { get; set; } = "";
}
