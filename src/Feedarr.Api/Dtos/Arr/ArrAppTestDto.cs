namespace Feedarr.Api.Dtos.Arr;

public sealed class ArrAppTestRequestDto
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

public sealed class ArrAppTestResultDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public long LatencyMs { get; set; }
    public string? Version { get; set; }
    public string? AppName { get; set; }
}
