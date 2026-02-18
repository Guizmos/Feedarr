using Feedarr.Api.Dtos.Updates;

namespace Feedarr.Api.Services.Updates;

public static class UpdateDtoMapper
{
    public static UpdateCheckDto ToDto(UpdateCheckResult result)
    {
        var releases = (result.Releases ?? Array.Empty<LatestReleaseInfo>())
            .Select(r => new LatestReleaseDto
            {
                TagName = r.TagName,
                Name = r.Name,
                Body = r.Body,
                PublishedAt = r.PublishedAt,
                HtmlUrl = r.HtmlUrl,
                IsPrerelease = r.IsPrerelease
            })
            .ToList();

        return new UpdateCheckDto
        {
            Enabled = result.Enabled,
            CurrentVersion = result.CurrentVersion,
            IsUpdateAvailable = result.IsUpdateAvailable,
            CheckIntervalHours = result.CheckIntervalHours,
            LatestRelease = result.LatestRelease is null
                ? null
                : new LatestReleaseDto
                {
                    TagName = result.LatestRelease.TagName,
                    Name = result.LatestRelease.Name,
                    Body = result.LatestRelease.Body,
                    PublishedAt = result.LatestRelease.PublishedAt,
                    HtmlUrl = result.LatestRelease.HtmlUrl,
                    IsPrerelease = result.LatestRelease.IsPrerelease
                },
            Releases = releases
        };
    }
}
