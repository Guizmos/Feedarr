namespace Feedarr.Api.Dtos.Updates;

public sealed class UpdateCheckDto
{
    public bool Enabled { get; set; }
    public string CurrentVersion { get; set; } = "";
    public bool IsUpdateAvailable { get; set; }
    public int CheckIntervalHours { get; set; }
    public LatestReleaseDto? LatestRelease { get; set; }
    public IReadOnlyList<LatestReleaseDto> Releases { get; set; } = Array.Empty<LatestReleaseDto>();
}
