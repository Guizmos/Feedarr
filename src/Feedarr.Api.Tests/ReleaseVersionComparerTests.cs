using Feedarr.Api.Services.Updates;

namespace Feedarr.Api.Tests;

public sealed class ReleaseVersionComparerTests
{
    [Theory]
    [InlineData("1.2.3")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3-beta.1")]
    [InlineData("v1.2.3-rc.2+build.5")]
    public void TryParse_Accepts_ValidSemVer(string input)
    {
        var ok = ReleaseVersionComparer.TryParse(input, out var version);

        Assert.True(ok);
        Assert.NotNull(version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("version-1.2.3")]
    [InlineData("v1.2.x")]
    public void TryParse_Rejects_InvalidSemVer(string input)
    {
        var ok = ReleaseVersionComparer.TryParse(input, out _);

        Assert.False(ok);
    }

    [Fact]
    public void Compare_Treats_Stable_As_Newer_Than_Prerelease()
    {
        var parsedStable = ReleaseVersionComparer.TryParse("1.2.3", out var stable);
        var parsedRc = ReleaseVersionComparer.TryParse("1.2.3-rc.1", out var rc);

        Assert.True(parsedStable);
        Assert.True(parsedRc);
        Assert.True(ReleaseVersionComparer.Compare(stable, rc) > 0);
    }

    [Fact]
    public void IsUpdateAvailable_Handles_VPrefix_And_Prerelease_Filter()
    {
        Assert.True(ReleaseVersionComparer.IsUpdateAvailable("1.2.2", "v1.2.3", allowPrerelease: false));
        Assert.False(ReleaseVersionComparer.IsUpdateAvailable("1.2.3", "v1.2.3-rc.1", allowPrerelease: false));
        Assert.True(ReleaseVersionComparer.IsUpdateAvailable("1.2.2", "v1.2.3-rc.1", allowPrerelease: true));
    }
}
