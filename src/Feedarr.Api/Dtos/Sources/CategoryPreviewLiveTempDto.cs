namespace Feedarr.Api.Dtos.Sources;

public sealed class CategoryPreviewLiveTempDto
{
    public long? ProviderId { get; set; }
    public string TorznabUrl { get; set; } = string.Empty;
    public string? IndexerId { get; set; }
    public string? AuthMode { get; set; }
    public string? ApiKey { get; set; }
    public int CatId { get; set; }
    public int? Limit { get; set; }
    public string? SourceName { get; set; }
}
