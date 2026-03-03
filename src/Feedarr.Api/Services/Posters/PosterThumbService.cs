using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Feedarr.Api.Services.Posters;

/// <summary>
/// Generates WebP thumbnails at fixed widths from an existing source image.
/// Writes atomically (.tmp → rename) to avoid serving partial files.
/// Never upscales images beyond their original width.
/// </summary>
public sealed class PosterThumbService
{
    public static readonly int[] SupportedWidths = [342, 500, 780];

    private readonly ILogger<PosterThumbService> _logger;

    public PosterThumbService(ILogger<PosterThumbService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Generates all supported thumbnail widths from <paramref name="sourceBytes"/>,
    /// writing WebP files into <paramref name="storeDir"/>.
    /// Skips thumbnails that already exist on disk.
    /// Returns the list of widths successfully generated.
    /// </summary>
    public async Task<IReadOnlyList<int>> GenerateThumbsAsync(
        byte[] sourceBytes,
        string storeDir,
        CancellationToken ct)
    {
        if (sourceBytes is null || sourceBytes.Length == 0) return [];
        if (!Directory.Exists(storeDir)) return [];

        var generated = new List<int>();

        try
        {
            using var image = Image.Load(sourceBytes);
            var originalWidth = image.Width;

            foreach (var targetWidth in SupportedWidths)
            {
                ct.ThrowIfCancellationRequested();

                var thumbPath = Path.Combine(storeDir, $"w{targetWidth}.webp");
                if (File.Exists(thumbPath)) continue;

                // Never upscale
                var effectiveWidth = Math.Min(targetWidth, originalWidth);

                try
                {
                    var thumbBytes = await ResizeToWebPAsync(image, effectiveWidth, ct).ConfigureAwait(false);
                    await WriteAtomicAsync(thumbPath, thumbBytes, ct).ConfigureAwait(false);
                    generated.Add(targetWidth);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate thumb w{Width} in {Dir}", targetWidth, storeDir);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load source image for thumb generation in {Dir}", storeDir);
        }

        return generated;
    }

    /// <summary>
    /// Generates a single thumbnail at <paramref name="targetWidth"/> from an existing
    /// <paramref name="sourceFile"/> path. Returns the WebP bytes or null on failure.
    /// </summary>
    public async Task<byte[]?> GenerateSingleThumbAsync(string sourceFile, int targetWidth, CancellationToken ct)
    {
        if (!File.Exists(sourceFile)) return null;

        try
        {
            var sourceBytes = await File.ReadAllBytesAsync(sourceFile, ct).ConfigureAwait(false);
            using var image = Image.Load(sourceBytes);
            var effectiveWidth = Math.Min(targetWidth, image.Width);
            return await ResizeToWebPAsync(image, effectiveWidth, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate single thumb w{Width} from {Source}", targetWidth, sourceFile);
            return null;
        }
    }

    private static async Task<byte[]> ResizeToWebPAsync(Image image, int targetWidth, CancellationToken ct)
    {
        // Clone so the original image is not mutated between widths
        using var clone = image.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max,
            Size = new Size(targetWidth, 0), // height = 0 → auto (keep aspect ratio)
        }));

        using var ms = new MemoryStream();
        var encoder = new WebpEncoder { Quality = 80, Method = WebpEncodingMethod.Default };
        await clone.SaveAsync(ms, encoder, ct).ConfigureAwait(false);
        return ms.ToArray();
    }

    private static async Task WriteAtomicAsync(string targetPath, byte[] bytes, CancellationToken ct)
    {
        var tmpPath = targetPath + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tmpPath, bytes, ct).ConfigureAwait(false);
            File.Move(tmpPath, targetPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { /* best-effort cleanup */ }
            throw;
        }
    }
}
