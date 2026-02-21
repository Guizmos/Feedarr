namespace Feedarr.Api.Dtos.Providers;

public sealed class ExternalProviderDefinitionDto
{
    public string ProviderKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? DefaultBaseUrl { get; set; }
    public ExternalProviderUiHintsDto UiHints { get; set; } = new();
    public IReadOnlyList<ExternalProviderFieldSchemaDto> FieldsSchema { get; set; } = Array.Empty<ExternalProviderFieldSchemaDto>();
}
