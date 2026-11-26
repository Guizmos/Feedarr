using System.Text.Json.Serialization;

namespace Feedarr.Api.Models;

public sealed class DirectRssFeed
{
    public long Id { get; set; }
    public long SourceId { get; set; }
    [JsonIgnore] public string Url { get; set; } = "";
    public string? Name { get; set; }
    public string CategoryKey { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool HasUrl => !string.IsNullOrEmpty(Url);
    public string? LastStatus { get; set; }
    public string? LastError { get; set; }
    public long? LastCheckedAt { get; set; }
    public int? LastItemCount { get; set; }
    public bool PotentiallySaturated { get; set; }
}
