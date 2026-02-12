namespace Feedarr.Api.Dtos.Sources;

public sealed class SourceCategoriesUpdateDto
{
    public List<SourceCategorySelectionDto> Categories { get; set; } = new();
}
