namespace Feedarr.Api.Dtos.Providers;

public sealed class ExternalProviderCreateDto
{
    public string ProviderKey { get; set; } = "";
    public string? DisplayName { get; set; }
    public bool? Enabled { get; set; }
    public string? BaseUrl { get; set; }
    public Dictionary<string, string?>? Auth { get; set; }
    public Dictionary<string, object?>? Options { get; set; }
}
