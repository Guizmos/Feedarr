using SkiaSharp;

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

        try
        {
            using var image = SKBitmap.Decode(sourceBytes)
                ?? throw new InvalidDataException("Unsupported or invalid source image.");
            return await GenerateThumbsAsync(image, storeDir, SupportedWidths, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load source image for thumb generation in {Dir}", storeDir);
        }

        return [];
    }

    /// <summary>
    /// Generates thumbnail widths from an existing original file, writing WebP files into
    /// <paramref name="storeDir"/>. Skips thumbnails that already exist on disk.
    /// Returns the list of widths successfully generated.
    /// </summary>
    public async Task<IReadOnlyList<int>> GenerateThumbsAsync(
        string sourceFile,
        string storeDir,
        IReadOnlyList<int>? targetWidths,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceFile) || !File.Exists(sourceFile)) return [];
        if (!Directory.Exists(storeDir)) return [];

        try
        {
            var sourceBytes = await File.ReadAllBytesAsync(sourceFile, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            using var image = SKBitmap.Decode(sourceBytes)
                ?? throw new InvalidDataException("Unsupported or invalid source image.");
            return await GenerateThumbsAsync(image, storeDir, NormalizeWidths(targetWidths), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load source image for thumb generation from {Source}", sourceFile);
            return [];
        }
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
            ct.ThrowIfCancellationRequested();
            using var image = SKBitmap.Decode(sourceBytes)
                ?? throw new InvalidDataException("Unsupported or invalid source image.");
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

    private static Task<byte[]> ResizeToWebPAsync(SKBitmap image, int targetWidth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var targetHeight = Math.Max(
            1,
            (int)Math.Round(image.Height * (targetWidth / (double)image.Width), MidpointRounding.AwayFromZero));
        var targetInfo = new SKImageInfo(targetWidth, targetHeight, image.ColorType, image.AlphaType, image.ColorSpace);
        using var resized = image.Resize(targetInfo, new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidOperationException($"Failed to resize image to {targetWidth}x{targetHeight}.");

        ct.ThrowIfCancellationRequested();
        using var encodedImage = SKImage.FromBitmap(resized);
        using var encodedData = encodedImage.Encode(SKEncodedImageFormat.Webp, 80)
            ?? throw new InvalidOperationException("Failed to encode WebP thumbnail.");
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(encodedData.ToArray());
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

    private async Task<IReadOnlyList<int>> GenerateThumbsAsync(
        SKBitmap image,
        string storeDir,
        IReadOnlyList<int> targetWidths,
        CancellationToken ct)
    {
        var generated = new List<int>();
        var originalWidth = image.Width;

        foreach (var targetWidth in targetWidths)
        {
            ct.ThrowIfCancellationRequested();

            var thumbPath = Path.Combine(storeDir, $"w{targetWidth}.webp");
            if (File.Exists(thumbPath)) continue;

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

        return generated;
    }

    private static IReadOnlyList<int> NormalizeWidths(IReadOnlyList<int>? widths)
    {
        if (widths is null || widths.Count == 0)
            return SupportedWidths;

        return widths
            .Distinct()
            .Where(width => SupportedWidths.Contains(width))
            .OrderBy(width => width)
            .ToArray();
    }
}
