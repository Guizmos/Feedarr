namespace Feedarr.Api.Dtos.Providers;

public sealed class ProviderTestRequestDto
{
    public string Type { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
}
