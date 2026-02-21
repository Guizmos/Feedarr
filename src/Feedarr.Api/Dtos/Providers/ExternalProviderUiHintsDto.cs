namespace Feedarr.Api.Dtos.Providers;

public sealed class ExternalProviderUiHintsDto
{
    public string Icon { get; set; } = "";
    public IReadOnlyList<string> Badges { get; set; } = Array.Empty<string>();
}
