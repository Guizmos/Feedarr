namespace Feedarr.Api.Dtos.System;

public sealed class SystemStatusDto
{
    public string AppName { get; set; } = "Feedarr";
    public string Version { get; set; } = "";
    public string Environment { get; set; } = "";
    public long UptimeSeconds { get; set; }

    public string DataDir { get; set; } = "";
    public string DbPath { get; set; } = "";
    public double DbSizeMB { get; set; }

    public int SourcesCount { get; set; }
    public int ReleasesCount { get; set; }
    public long? ReleasesLatestTs { get; set; }
    public int? ReleasesNewSinceTsCount { get; set; }

    public long? LastSyncAtTs { get; set; }
}
