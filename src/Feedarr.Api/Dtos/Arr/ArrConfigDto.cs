namespace Feedarr.Api.Dtos.Arr;

public sealed class ArrRootFolderDto
{
    public int Id { get; set; }
    public string Path { get; set; } = "";
    public long FreeSpace { get; set; }
}

public sealed class ArrQualityProfileDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class ArrTagDto
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
}

public sealed class ArrConfigResponseDto
{
    public List<ArrRootFolderDto> RootFolders { get; set; } = new();
    public List<ArrQualityProfileDto> QualityProfiles { get; set; } = new();
    public List<ArrTagDto> Tags { get; set; } = new();
}
