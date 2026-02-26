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
    public bool IsStandard { get; set; }
    public bool IsSupported { get; set; }
    public string? AssignedGroupKey { get; set; }
    public string? AssignedGroupLabel { get; set; }
    public bool IsAssigned { get; set; }
}
