using Feedarr.Api.Services.Posters;

namespace Feedarr.Api.Tests;

public sealed class PosterPathResolverTests
{
    [Fact]
    public void TryResolvePosterFile_AllowsPlainFilenameInsideRoot()
    {
        using var workspace = new TestWorkspace();
        var resolver = new PosterPathResolver(workspace.RootDir);

        var resolved = resolver.TryResolvePosterFile("poster.jpg", out var fullPath);

        Assert.True(resolved);
        Assert.Equal(Path.Combine(workspace.RootDir, "poster.jpg"), fullPath);
    }

    [Theory]
    [InlineData("../a.jpg")]
    [InlineData("..\\a.jpg")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    public void TryResolvePosterFile_RejectsTraversalAndRootedPaths(string input)
    {
        using var workspace = new TestWorkspace();
        var resolver = new PosterPathResolver(workspace.RootDir);

        var resolved = resolver.TryResolvePosterFile(input, out var fullPath);

        Assert.False(resolved);
        Assert.Equal(string.Empty, fullPath);
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            RootDir = Path.Combine(Path.GetTempPath(), "feedarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootDir);
        }

        public string RootDir { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootDir))
                    Directory.Delete(RootDir, true);
            }
            catch
            {
            }
        }
    }
}
