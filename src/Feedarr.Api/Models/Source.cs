namespace Feedarr.Api.Models;

/// <summary>
/// Représente une source complète (avec API key pour usage interne).
/// </summary>
public sealed class Source
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string TorznabUrl { get; set; } = "";
    public string? ApiKey { get; set; }
    public string AuthMode { get; set; } = "query";
    public string? RssMode { get; set; }
    public long? LastSyncAt { get; set; }
    public long? ProviderId { get; set; }
    public string? Color { get; set; }
}
