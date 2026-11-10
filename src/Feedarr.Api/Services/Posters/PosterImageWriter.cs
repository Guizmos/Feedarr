using SkiaSharp;

namespace Feedarr.Api.Services.Posters;

internal static class PosterImageWriter
{
    internal static async Task WriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        // Decode before creating/replacing any published file. A MIME type,
        // extension or signature alone does not establish that this is an image.
        ct.ThrowIfCancellationRequested();
        using var stream = new SKMemoryStream(bytes);
        using var codec = SKCodec.Create(stream);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
            (long)codec.Info.Width * codec.Info.Height > 40_000_000)
            throw new InvalidDataException("Downloaded poster is not a supported image (maximum 40 megapixels)");
        using var bitmap = new SKBitmap(codec.Info);
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
            throw new InvalidDataException("Downloaded poster image is corrupt or incomplete");
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
    }
}
