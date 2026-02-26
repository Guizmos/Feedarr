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
            UnifiedCategory.Emission => "emissions",
            UnifiedCategory.Spectacle => "spectacle",
            UnifiedCategory.JeuWindows => "games",
            UnifiedCategory.Animation => "animation",
            UnifiedCategory.Anime => "anime",
            UnifiedCategory.Audio => "audio",
            UnifiedCategory.Book => "books",
            UnifiedCategory.Comic => "comics",
            _ => "other"
        };
    }

    public static string ToLabel(UnifiedCategory category)
    {
        return category switch
        {
            UnifiedCategory.Film => "Films",
            UnifiedCategory.Serie => "Série TV",
            UnifiedCategory.Emission => "Emissions",
            UnifiedCategory.Spectacle => "Spectacle",
            UnifiedCategory.JeuWindows => "Jeux PC",
            UnifiedCategory.Animation => "Animation",
            UnifiedCategory.Anime => "Anime",
            UnifiedCategory.Audio => "Audio",
            UnifiedCategory.Book => "Livres",
            UnifiedCategory.Comic => "Comics",
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
            UnifiedCategory.Anime => "anime",
            UnifiedCategory.Audio => "audio",
            UnifiedCategory.Book => "book",
            UnifiedCategory.Comic => "comic",
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
        if (!CategoryGroupCatalog.TryNormalizeKey(key, out var canonicalKey))
            return false;

        switch (canonicalKey)
        {
            case "films":
                category = UnifiedCategory.Film;
                return true;
            case "series":
                category = UnifiedCategory.Serie;
                return true;
            case "emissions":
                category = UnifiedCategory.Emission;
                return true;
            case "spectacle":
                category = UnifiedCategory.Spectacle;
                return true;
            case "games":
                category = UnifiedCategory.JeuWindows;
                return true;
            case "animation":
                category = UnifiedCategory.Animation;
                return true;
            case "anime":
                category = UnifiedCategory.Anime;
                return true;
            case "audio":
                category = UnifiedCategory.Audio;
                return true;
            case "book":
            case "books":
                category = UnifiedCategory.Book;
                return true;
            case "comic":
            case "comics":
                category = UnifiedCategory.Comic;
                return true;
        }

        return false;
    }
}
