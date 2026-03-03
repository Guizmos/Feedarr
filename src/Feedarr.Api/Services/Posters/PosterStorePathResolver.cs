namespace Feedarr.Api.Services.Posters;

/// <summary>
/// Resolves paths inside the canonical poster store (posters/store/{storeDir}/{file}).
/// Unlike <see cref="PosterPathResolver"/> this resolver accepts one level of subdirectory,
/// but still prevents any path-traversal attempt.
/// </summary>
internal sealed class PosterStorePathResolver
{
    private readonly string _storeRoot;
    private readonly string _storeRootWithSeparator;

    public PosterStorePathResolver(string storeRoot)
    {
        _storeRoot = string.IsNullOrWhiteSpace(storeRoot)
            ? string.Empty
            : Path.GetFullPath(storeRoot);
        _storeRootWithSeparator = _storeRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _storeRoot
            : _storeRoot + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Validates <paramref name="storeDir"/> (e.g. "tmdb-550") and returns the
    /// absolute path to that subdirectory inside the store root.
    /// Returns false when the value is empty or contains traversal attempts.
    /// </summary>
    public bool TryResolveStoreDir(string storeDir, out string fullDirPath)
    {
        fullDirPath = string.Empty;
        if (string.IsNullOrWhiteSpace(_storeRoot)) return false;

        var dir = storeDir?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dir)) return false;

        // storeDir must be a single path component — no separators or colons
        if (dir.Contains('/') || dir.Contains('\\') || dir.Contains(':')) return false;
        if (dir.Contains("..", StringComparison.Ordinal)) return false;
        if (dir.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;

        var resolved = Path.GetFullPath(Path.Combine(_storeRoot, dir));
        if (!resolved.StartsWith(_storeRootWithSeparator, StringComparison.OrdinalIgnoreCase))
            return false;

        fullDirPath = resolved;
        return true;
    }

    /// <summary>
    /// Validates <paramref name="fileName"/> (e.g. "w500.webp") and combines it
    /// with the already-resolved <paramref name="fullDirPath"/> to produce the
    /// absolute file path. Returns false on any validation failure.
    /// </summary>
    public bool TryResolveFile(string fullDirPath, string fileName, out string fullFilePath)
    {
        fullFilePath = string.Empty;
        if (string.IsNullOrWhiteSpace(fullDirPath)) return false;

        var name = fileName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return false;

        if (name.Contains('/') || name.Contains('\\') || name.Contains(':')) return false;
        if (name.Contains("..", StringComparison.Ordinal)) return false;
        if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;

        var resolved = Path.GetFullPath(Path.Combine(fullDirPath, name));

        // The file must sit directly inside fullDirPath (one level deep inside the store root)
        var dirWithSep = fullDirPath.EndsWith(Path.DirectorySeparatorChar)
            ? fullDirPath
            : fullDirPath + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(dirWithSep, StringComparison.OrdinalIgnoreCase))
            return false;

        fullFilePath = resolved;
        return true;
    }

    /// <summary>
    /// Convenience: resolve storeDir + fileName in one call.
    /// </summary>
    public bool TryResolvePosterFile(string storeDir, string fileName, out string fullFilePath)
    {
        fullFilePath = string.Empty;
        return TryResolveStoreDir(storeDir, out var dir) && TryResolveFile(dir, fileName, out fullFilePath);
    }

    /// <summary>
    /// Sanitise a raw provider+id string so it is safe to use as a storeDir name.
    /// Keeps alphanumeric, hyphens, underscores and dots; replaces everything else with '-'.
    /// </summary>
    public static string SanitizeStoreDir(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var chars = raw.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.') continue;
            chars[i] = '-';
        }
        return new string(chars).Trim('-');
    }
}
