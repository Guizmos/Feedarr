namespace Feedarr.Api.Dtos.Providers;

public sealed class ExternalProviderUpdateDto
{
    public string? DisplayName { get; set; }
    public bool? Enabled { get; set; }
    public string? BaseUrl { get; set; }
    public Dictionary<string, string?>? Auth { get; set; }
    public Dictionary<string, object?>? Options { get; set; }
}
