namespace Feedarr.Api.Dtos.Updates;

public sealed class LatestReleaseDto
{
    public string TagName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTimeOffset? PublishedAt { get; set; }
    public string HtmlUrl { get; set; } = "";
    public bool IsPrerelease { get; set; }
}
