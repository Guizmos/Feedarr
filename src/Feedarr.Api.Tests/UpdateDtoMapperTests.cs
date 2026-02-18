using Feedarr.Api.Services.Updates;

namespace Feedarr.Api.Tests;

public sealed class UpdateDtoMapperTests
{
    [Fact]
    public void ToDto_Maps_All_Fields()
    {
        var publishedAt = DateTimeOffset.Parse("2026-02-15T12:00:00Z");
        var result = new UpdateCheckResult(
            Enabled: true,
            CurrentVersion: "1.2.0",
            IsUpdateAvailable: true,
            CheckIntervalHours: 6,
            LatestRelease: new LatestReleaseInfo(
                TagName: "v1.3.0",
                Name: "v1.3.0",
                Body: "## Changelog",
                PublishedAt: publishedAt,
                HtmlUrl: "https://github.com/acme/feedarr/releases/tag/v1.3.0",
                IsPrerelease: false),
            Releases: new[]
            {
                new LatestReleaseInfo(
                    TagName: "v1.3.0",
                    Name: "v1.3.0",
                    Body: "## Changelog",
                    PublishedAt: publishedAt,
                    HtmlUrl: "https://github.com/acme/feedarr/releases/tag/v1.3.0",
                    IsPrerelease: false)
            });

        var dto = UpdateDtoMapper.ToDto(result);

        Assert.True(dto.Enabled);
        Assert.Equal("1.2.0", dto.CurrentVersion);
        Assert.True(dto.IsUpdateAvailable);
        Assert.Equal(6, dto.CheckIntervalHours);
        Assert.NotNull(dto.LatestRelease);
        Assert.Equal("v1.3.0", dto.LatestRelease!.TagName);
        Assert.Equal("v1.3.0", dto.LatestRelease.Name);
        Assert.Equal("## Changelog", dto.LatestRelease.Body);
        Assert.Equal(publishedAt, dto.LatestRelease.PublishedAt);
        Assert.Equal("https://github.com/acme/feedarr/releases/tag/v1.3.0", dto.LatestRelease.HtmlUrl);
        Assert.False(dto.LatestRelease.IsPrerelease);
        Assert.Single(dto.Releases);
        Assert.Equal("v1.3.0", dto.Releases[0].TagName);
    }

    [Fact]
    public void ToDto_Handles_MissingRelease()
    {
        var result = new UpdateCheckResult(
            Enabled: false,
            CurrentVersion: "1.0.0",
            IsUpdateAvailable: false,
            CheckIntervalHours: 12,
            LatestRelease: null,
            Releases: Array.Empty<LatestReleaseInfo>());

        var dto = UpdateDtoMapper.ToDto(result);

        Assert.False(dto.Enabled);
        Assert.Equal("1.0.0", dto.CurrentVersion);
        Assert.False(dto.IsUpdateAvailable);
        Assert.Equal(12, dto.CheckIntervalHours);
        Assert.Null(dto.LatestRelease);
        Assert.Empty(dto.Releases);
    }
}
