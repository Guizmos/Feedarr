namespace Feedarr.Api.Dtos.Sources;

public sealed class SourceTestRequestDto
{
    public string TorznabUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string AuthMode { get; set; } = "query"; // query|header
    public int RssLimit { get; set; } = 50;
}
