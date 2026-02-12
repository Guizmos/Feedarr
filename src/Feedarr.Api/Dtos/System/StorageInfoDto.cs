namespace Feedarr.Api.Dtos.System;

public sealed class StorageInfoDto
{
    public List<DiskVolumeDto> Volumes { get; set; } = new();
    public StorageUsageDto Usage { get; set; } = new();
}

public sealed class DiskVolumeDto
{
    public string Path { get; set; } = "";
    public string Label { get; set; } = "";
    public long FreeBytes { get; set; }
    public long TotalBytes { get; set; }
}

public sealed class StorageUsageDto
{
    public long DatabaseBytes { get; set; }
    public long PostersBytes { get; set; }
    public long BackupsBytes { get; set; }
    public int PostersCount { get; set; }
    public int BackupsCount { get; set; }
}
