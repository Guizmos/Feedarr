namespace Feedarr.Api.Models;

/// <summary>
/// Représente un provider complet (Jackett/Prowlarr) avec API key.
/// </summary>
public sealed class Provider
{
    public long Id { get; set; }
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string? ApiKey { get; set; }
    public bool Enabled { get; set; }
    public long? LastTestOkAt { get; set; }
    public long? CreatedAt { get; set; }
    public long? UpdatedAt { get; set; }
}
