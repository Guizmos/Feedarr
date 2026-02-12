using System.Globalization;
using System.Text;

namespace Feedarr.Api.Services.Categories;

public sealed class UnifiedCategoryService
{
    public sealed class UnifiedCategory
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
    }

    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["films"] = "Films",
        ["series"] = "Series TV",
        ["anime"] = "Animation",
        ["games"] = "Jeux",
        ["shows"] = "Emissions",
        ["spectacle"] = "Spectacle"
    };

    public UnifiedCategory? Get(string? categoryName, string? title)
    {
        var key = GetKey(categoryName, title);
        if (string.IsNullOrWhiteSpace(key)) return null;
        return new UnifiedCategory { Key = key, Label = Labels[key] };
    }

    public string? GetKey(string? categoryName, string? title)
    {
        var cat = Normalize(categoryName);
        var t = Normalize(title);

        if (ContainsAny(t, new[]
            { "spectacle", "concert", "opera", "theatre", "ballet", "symphonie", "orchestr", "philharmon", "ring", "choregraph", "danse" }))
            return "spectacle";

        if (ContainsAny(t, new[]
            { "emission", "enquete", "magazine", "talk", "show", "reportage", "documentaire", "docu", "quotidien", "quotidienne" }))
            return "shows";

        if (string.IsNullOrWhiteSpace(cat)) return null;

        if (ContainsAny(cat, new[] { "pc/games", "pc games", "game", "games", "jeu", "jeux" })) return "games";
        if (ContainsAny(cat, new[] { "tv/anime", "anime" })) return "anime";
        if (ContainsAny(cat, new[] { "movies/other" })) return "anime";
        if (ContainsAny(cat, new[] { "spectacle", "concert", "opera", "theatre", "ballet" })) return "spectacle";
        if (ContainsAny(cat, new[] { "documentary", "documentaire", "doc", "emission", "show", "magazine" }))
            return "shows";
        if (cat == "movies" || cat.StartsWith("movie", StringComparison.OrdinalIgnoreCase)) return "films";
        if (cat == "tv" || cat.StartsWith("tv/", StringComparison.OrdinalIgnoreCase)) return "series";

        return null;
    }

    private static bool ContainsAny(string? value, IEnumerable<string> needles)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return needles.Any(value.Contains);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var normalized = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var chars = normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark);
        return new string(chars.ToArray()).Replace("  ", " ").Trim();
    }
}
