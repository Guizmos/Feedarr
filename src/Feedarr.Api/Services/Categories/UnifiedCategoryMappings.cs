using Feedarr.Api.Models;

namespace Feedarr.Api.Services.Categories;

public static class UnifiedCategoryMappings
{
    public static string ToKey(UnifiedCategory category)
    {
        return category switch
        {
            UnifiedCategory.Film => "films",
            UnifiedCategory.Serie => "series",
            UnifiedCategory.Emission => "shows",
            UnifiedCategory.Spectacle => "spectacle",
            UnifiedCategory.JeuWindows => "games",
            UnifiedCategory.Animation => "anime",
            _ => "other"
        };
    }

    public static string ToLabel(UnifiedCategory category)
    {
        return category switch
        {
            UnifiedCategory.Film => "Films",
            UnifiedCategory.Serie => "Series TV",
            UnifiedCategory.Emission => "Emissions",
            UnifiedCategory.Spectacle => "Spectacle",
            UnifiedCategory.JeuWindows => "Jeux PC",
            UnifiedCategory.Animation => "Animation",
            _ => "Autre"
        };
    }

    public static string ToMediaType(UnifiedCategory category)
    {
        return category switch
        {
            UnifiedCategory.Film => "movie",
            UnifiedCategory.Spectacle => "movie",
            UnifiedCategory.Serie => "series",
            UnifiedCategory.Emission => "series",
            UnifiedCategory.JeuWindows => "game",
            UnifiedCategory.Animation => "movie",
            _ => "unknown"
        };
    }

    public static bool TryParse(string? value, out UnifiedCategory category)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            Enum.TryParse(value.Trim(), ignoreCase: true, out UnifiedCategory parsed))
        {
            category = parsed;
            return true;
        }

        category = UnifiedCategory.Autre;
        return false;
    }

    public static bool TryParseKey(string? key, out UnifiedCategory category)
    {
        category = UnifiedCategory.Autre;
        if (string.IsNullOrWhiteSpace(key)) return false;

        switch (key.Trim().ToLowerInvariant())
        {
            case "films":
                category = UnifiedCategory.Film;
                return true;
            case "series":
                category = UnifiedCategory.Serie;
                return true;
            case "shows":
                category = UnifiedCategory.Emission;
                return true;
            case "spectacle":
                category = UnifiedCategory.Spectacle;
                return true;
            case "games":
                category = UnifiedCategory.JeuWindows;
                return true;
            case "anime":
                category = UnifiedCategory.Animation;
                return true;
        }

        return false;
    }
}
