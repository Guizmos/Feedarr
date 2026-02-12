namespace Feedarr.Api.Dtos.System;

public sealed class BackupFileDto
{
    public string Name { get; set; } = "";
    public long SizeBytes { get; set; }
    public long CreatedAtTs { get; set; }
}
