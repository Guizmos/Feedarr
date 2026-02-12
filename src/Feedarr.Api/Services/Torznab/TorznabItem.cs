namespace Feedarr.Api.Services.Torznab;

public sealed class TorznabItem
{
    public string Guid { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Link { get; set; }
    public string? DownloadUrl { get; set; }

    public long? PublishedAtTs { get; set; }

    public long? SizeBytes { get; set; }
    public int? Seeders { get; set; }
    public int? Leechers { get; set; }
    public int? Grabs { get; set; }

    public string? InfoHash { get; set; }

    public int? CategoryId { get; set; } // on garde 1 catégorie principale V1
    public List<int> CategoryIds { get; set; } = new();
    public int? StdCategoryId { get; set; }
    public int? SpecCategoryId { get; set; }
    public Dictionary<string, string> Attrs { get; set; } = new(); // debug/extra
}
