namespace Feedarr.Api.Dtos.Sources;

public sealed class SourceUpdateDto
{
    public string? Name { get; set; }
    public string? TorznabUrl { get; set; }
    public string? ApiKey { get; set; }   // optionnel: si null/empty => on ne touche pas
    public string? AuthMode { get; set; } // query/header
    public string? Color { get; set; }
}
