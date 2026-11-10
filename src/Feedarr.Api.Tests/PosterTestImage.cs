using SkiaSharp;

namespace Feedarr.Api.Tests;

internal static class PosterTestImage
{
    internal static byte[] Create()
    {
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.Navy);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
