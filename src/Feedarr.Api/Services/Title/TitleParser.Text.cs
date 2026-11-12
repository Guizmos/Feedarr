using System.Text.RegularExpressions;
using Feedarr.Api.Models;

namespace Feedarr.Api.Services.Titles;

public sealed partial class TitleParser
{
    private static string NormalizeResolution(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v == "4k") return "2160p";
        return v;
    }

    private static int? TryInt(string s) => int.TryParse(s, out var n) ? n : null;

    private static string FallbackTitle(string raw)
    {
        var s = (raw ?? "").Trim();
        s = Regex.Replace(s, @"\[[^\]]*\]", " ");
        s = s.Replace('.', ' ').Replace('_', ' ').Replace('+', ' ');
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        return string.IsNullOrWhiteSpace(s) ? (raw ?? "").Trim() : s;
    }
}
