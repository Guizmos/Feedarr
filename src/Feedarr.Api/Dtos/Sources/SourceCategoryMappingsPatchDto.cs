using System.Text.Json.Serialization;

namespace Feedarr.Api.Dtos.Sources;

public sealed class SourceCategoryMappingsPatchDto
{
    public List<SourceCategoryMappingPatchItemDto> Mappings { get; set; } = new();

    [JsonPropertyName("selectedCategoryIds")]
    public List<int>? SelectedCategoryIds { get; set; }

    // Backward-compat for older clients still sending legacy fields.
    [JsonPropertyName("categoryIds")]
    public List<int>? CategoryIds { get; set; }

    // Backward-compat alias seen in some legacy payloads.
    [JsonPropertyName("activeCategoryIds")]
    public List<int>? ActiveCategoryIds { get; set; }
}

public sealed class SourceCategoryMappingPatchItemDto
{
    public int CatId { get; set; }
    public string? GroupKey { get; set; }
    public string? GroupLabel { get; set; }
}
