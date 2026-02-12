namespace Feedarr.Api.Models;

/// <summary>
/// Représente un provider pour affichage en liste (sans API key, avec compteur).
/// </summary>
public sealed class ProviderListItem
{
    public long Id { get; set; }
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public bool HasApiKey { get; set; }
    public bool Enabled { get; set; }
    public long? LastTestOkAt { get; set; }
    public long? CreatedAt { get; set; }
    public long? UpdatedAt { get; set; }
    public int LinkedSources { get; set; }
}
