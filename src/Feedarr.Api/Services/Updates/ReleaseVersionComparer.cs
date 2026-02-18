using System.Text.RegularExpressions;

namespace Feedarr.Api.Services.Updates;

public sealed record ReleaseSemVersion(int Major, int Minor, int Patch, IReadOnlyList<string> PreReleaseSegments)
{
    public bool IsPrerelease => PreReleaseSegments.Count > 0;
}

public static class ReleaseVersionComparer
{
    private static readonly Regex SemVerRegex = new(
        @"^\s*v?(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryParse(string? value, out ReleaseSemVersion version)
    {
        version = default!;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var match = SemVerRegex.Match(value);
        if (!match.Success)
            return false;

        if (!int.TryParse(match.Groups["major"].Value, out var major))
            return false;
        if (!int.TryParse(match.Groups["minor"].Value, out var minor))
            return false;
        if (!int.TryParse(match.Groups["patch"].Value, out var patch))
            return false;

        var pre = match.Groups["pre"].Success
            ? match.Groups["pre"].Value.Split('.', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();

        version = new ReleaseSemVersion(major, minor, patch, pre);
        return true;
    }

    public static int Compare(ReleaseSemVersion left, ReleaseSemVersion right)
    {
        var majorCmp = left.Major.CompareTo(right.Major);
        if (majorCmp != 0) return majorCmp;

        var minorCmp = left.Minor.CompareTo(right.Minor);
        if (minorCmp != 0) return minorCmp;

        var patchCmp = left.Patch.CompareTo(right.Patch);
        if (patchCmp != 0) return patchCmp;

        var leftPre = left.PreReleaseSegments;
        var rightPre = right.PreReleaseSegments;

        if (leftPre.Count == 0 && rightPre.Count == 0) return 0;
        if (leftPre.Count == 0) return 1;
        if (rightPre.Count == 0) return -1;

        var max = Math.Max(leftPre.Count, rightPre.Count);
        for (var i = 0; i < max; i++)
        {
            if (i >= leftPre.Count) return -1;
            if (i >= rightPre.Count) return 1;

            var leftToken = leftPre[i];
            var rightToken = rightPre[i];

            var leftIsNumeric = int.TryParse(leftToken, out var leftNum);
            var rightIsNumeric = int.TryParse(rightToken, out var rightNum);

            if (leftIsNumeric && rightIsNumeric)
            {
                var cmpNum = leftNum.CompareTo(rightNum);
                if (cmpNum != 0) return cmpNum;
                continue;
            }

            if (leftIsNumeric && !rightIsNumeric) return -1;
            if (!leftIsNumeric && rightIsNumeric) return 1;

            var cmpText = string.CompareOrdinal(leftToken, rightToken);
            if (cmpText != 0) return cmpText;
        }

        return 0;
    }

    public static bool IsUpdateAvailable(string? currentVersion, string? latestVersion, bool allowPrerelease)
    {
        if (!TryParse(currentVersion, out var current))
            return false;
        if (!TryParse(latestVersion, out var latest))
            return false;
        if (!allowPrerelease && latest.IsPrerelease)
            return false;

        return Compare(latest, current) > 0;
    }
}
