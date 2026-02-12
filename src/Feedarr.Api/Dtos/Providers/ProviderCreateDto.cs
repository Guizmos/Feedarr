namespace Feedarr.Api.Dtos.Providers;

public sealed class ProviderCreateDto
{
    public string Type { get; set; } = "";
    public string? Name { get; set; }
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public bool? Enabled { get; set; }
}
