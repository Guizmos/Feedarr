namespace Feedarr.Api.Dtos.Arr;

public sealed class ArrAddResponseDto
{
    public bool Ok { get; set; }
    public string Status { get; set; } = "";  // added | exists | fallback | error
    public string? OpenUrl { get; set; }
    public string? AppName { get; set; }
    public string? Message { get; set; }
}
