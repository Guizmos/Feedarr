namespace Feedarr.Api.Dtos.Sources;

public sealed class CategoryPreviewItemDto
{
    public long PublishedAtTs { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int CategoryId { get; set; }
    public string? ResultCategoryName { get; set; }
    public string? UnifiedCategory { get; set; }
    public int? TmdbId { get; set; }
    public int? TvdbId { get; set; }
    public int? Seeders { get; set; }
}
