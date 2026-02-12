namespace Feedarr.Api.Models;

/// <summary>
/// Représente une source pour affichage en liste (sans API key sensible).
/// </summary>
public sealed class SourceListItem
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string TorznabUrl { get; set; } = "";
    public string AuthMode { get; set; } = "query";
    public long? LastSyncAt { get; set; }
    public string? LastStatus { get; set; }
    public string? LastError { get; set; }
    public string? RssMode { get; set; }
    public long? ProviderId { get; set; }
    public string? Color { get; set; }
}
