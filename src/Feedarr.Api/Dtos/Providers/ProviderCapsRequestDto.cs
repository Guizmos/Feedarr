namespace Feedarr.Api.Dtos.Providers;

public sealed class ProviderCapsRequestDto
{
    public long ProviderId { get; set; }
    public long? SourceId { get; set; }
    public string? TorznabUrl { get; set; }
    public string? IndexerName { get; set; }
    public string? IndexerId { get; set; }
    public bool IncludeStandardCatalog { get; set; } = true;
    public bool IncludeSpecific { get; set; } = true;
}
