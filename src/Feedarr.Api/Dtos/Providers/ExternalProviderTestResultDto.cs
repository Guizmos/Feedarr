namespace Feedarr.Api.Dtos.Providers;

public sealed class ExternalProviderTestResultDto
{
    public bool Ok { get; set; }
    public long ElapsedMs { get; set; }
    public string? Error { get; set; }
}
