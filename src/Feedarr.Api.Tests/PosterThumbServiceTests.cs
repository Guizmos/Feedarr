using Feedarr.Api.Services.Posters;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Feedarr.Api.Tests;

public sealed class PosterThumbServiceTests
{
    [Fact]
    public async Task GenerateThumbsAsync_WritesWebPAtSupportedDimensions()
    {
        using var workspace = new ThumbWorkspace();
        var service = new PosterThumbService(NullLogger<PosterThumbService>.Instance);

        var generated = await service.GenerateThumbsAsync(
            CreateJpeg(1200, 1800),
            workspace.StoreDir,
            CancellationToken.None);

        Assert.Equal(PosterThumbService.SupportedWidths, generated);
        AssertWebP(workspace.ThumbPath(342), 342, 513);
        AssertWebP(workspace.ThumbPath(500), 500, 750);
        AssertWebP(workspace.ThumbPath(780), 780, 1170);
    }

    [Fact]
    public async Task GenerateThumbsAsync_DoesNotUpscaleAndPreservesAspectRatio()
    {
        using var workspace = new ThumbWorkspace();
        var service = new PosterThumbService(NullLogger<PosterThumbService>.Instance);

        var generated = await service.GenerateThumbsAsync(
            CreateJpeg(240, 360),
            workspace.StoreDir,
            CancellationToken.None);

        Assert.Equal(PosterThumbService.SupportedWidths, generated);
        foreach (var width in PosterThumbService.SupportedWidths)
            AssertWebP(workspace.ThumbPath(width), 240, 360);
    }

    [Fact]
    public async Task GenerateSingleThumbAsync_ReturnsWebPAtRequestedDimensions()
    {
        using var workspace = new ThumbWorkspace();
        var sourcePath = Path.Combine(workspace.StoreDir, "source.jpg");
        await File.WriteAllBytesAsync(sourcePath, CreateJpeg(1000, 1500));
        var service = new PosterThumbService(NullLogger<PosterThumbService>.Instance);

        var bytes = await service.GenerateSingleThumbAsync(sourcePath, 500, CancellationToken.None);

        Assert.NotNull(bytes);
        AssertWebP(bytes!, 500, 750);
    }

    private static byte[] CreateJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(38, 110, 190));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static void AssertWebP(string path, int expectedWidth, int expectedHeight)
    {
        Assert.True(File.Exists(path));
        AssertWebP(File.ReadAllBytes(path), expectedWidth, expectedHeight);
    }

    private static void AssertWebP(byte[] bytes, int expectedWidth, int expectedHeight)
    {
        Assert.True(bytes.Length > 12);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        using var bitmap = SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);
        Assert.Equal(expectedWidth, bitmap.Width);
        Assert.Equal(expectedHeight, bitmap.Height);
    }

    private sealed class ThumbWorkspace : IDisposable
    {
        public ThumbWorkspace()
        {
            StoreDir = Path.Combine(Path.GetTempPath(), "feedarr-thumb-service-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(StoreDir);
        }

        public string StoreDir { get; }

        public string ThumbPath(int width) => Path.Combine(StoreDir, $"w{width}.webp");

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(StoreDir)) Directory.Delete(StoreDir, recursive: true);
            }
            catch
            {
            }
        }
    }
}
