namespace Feedarr.Api.Services.Posters;

internal sealed class PosterPathResolver
{
    private readonly string _rootPath;
    private readonly string _rootPathWithSeparator;

    public PosterPathResolver(string rootPath)
    {
        _rootPath = string.IsNullOrWhiteSpace(rootPath)
            ? string.Empty
            : Path.GetFullPath(rootPath);
        _rootPathWithSeparator = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;
    }

    public bool TryResolvePosterFile(string input, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(_rootPath))
            return false;

        var candidate = input?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        if (Path.IsPathRooted(candidate))
            return false;

        if (!string.Equals(Path.GetFileName(candidate), candidate, StringComparison.Ordinal))
            return false;

        if (candidate.Contains("..", StringComparison.Ordinal))
            return false;

        if (candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;

        var resolvedPath = Path.GetFullPath(Path.Combine(_rootPath, candidate));
        if (!resolvedPath.StartsWith(_rootPathWithSeparator, StringComparison.OrdinalIgnoreCase))
            return false;

        fullPath = resolvedPath;
        return true;
    }
}
