namespace Feedarr.Api.Dtos.Providers;

public sealed class ProviderCapsRequestDto
{
    public long ProviderId { get; set; }
    public string? TorznabUrl { get; set; }
    public string? IndexerName { get; set; }
    public string? IndexerId { get; set; }
}
