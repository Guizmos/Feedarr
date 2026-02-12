namespace Feedarr.Api.Dtos.Categories;

public sealed class CapsCategoriesResponseDto
{
    public List<CapsCategoryDto> Categories { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public sealed class CapsCategoryDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsSub { get; set; }
    public int? ParentId { get; set; }
    public string UnifiedKey { get; set; } = "";
    public string UnifiedLabel { get; set; } = "";
    public bool IsRecommended { get; set; }
    public int? Score { get; set; }
    public string? Reason { get; set; }
}
