namespace Feedarr.Api.Dtos.Sources;

public sealed class SourceTestResultDto
{
    public bool Ok { get; set; }
    public long LatencyMs { get; set; }
    public string? Error { get; set; }

    public CapsInfo Caps { get; set; } = new();
    public RssInfo Rss { get; set; } = new();

    // =========================
    // CAPS (t=caps)
    // =========================
    public sealed class CapsInfo
    {
        public int CategoriesTotal { get; set; }
        public List<Cat> CategoriesTop { get; set; } = new();
        public List<Cat> Categories { get; set; } = new();

        public sealed class Cat
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public bool IsSub { get; set; }
            public int? ParentId { get; set; }
        }
    }

    // =========================
    // RSS / RECENT / SEARCH
    // =========================
    public sealed class RssInfo
    {
        public int ItemsCount { get; set; }
        public string? FirstTitle { get; set; }
        public long? FirstPublishedAt { get; set; }

        // 👉 NOUVEAU (important pour debug Jackett)
        public string? UsedMode { get; set; }

        // 👉 NOUVEAU : échantillon des items parsés
        public object? Sample { get; set; }
    }
}
