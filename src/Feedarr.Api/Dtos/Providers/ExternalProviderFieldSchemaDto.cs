namespace Feedarr.Api.Dtos.Providers;

public sealed class ExternalProviderFieldSchemaDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "text";
    public string? Placeholder { get; set; }
    public bool Required { get; set; }
    public bool Secret { get; set; }
    public string? SecretPlaceholder { get; set; }
}
