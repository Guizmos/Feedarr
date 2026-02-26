namespace Feedarr.Api.Services.Categories;

public sealed record StandardCatalogCategory(int Id, string Name, int? ParentId);

public static class StandardCategoryCatalog
{
    private static readonly StandardCatalogCategory[] Items =
    {
        // 1000 - Console
        new(1000, "Console", null),
        new(1010, "NDS", 1000),
        new(1020, "PSP", 1000),
        new(1030, "Wii", 1000),
        new(1040, "Xbox", 1000),
        new(1050, "Xbox 360", 1000),
        new(1060, "WiiWare", 1000),
        new(1070, "Xbox 360 DLC", 1000),
        new(1080, "PS3", 1000),
        new(1090, "Console/Other", 1000),
        new(1140, "Xbox One", 1000),
        new(1180, "PS4", 1000),

        // 2000 - Movies
        new(2000, "Movies", null),
        new(2010, "Movies/Foreign", 2000),
        new(2020, "Movies/Other", 2000),
        new(2030, "Movies/SD", 2000),
        new(2040, "Movies/HD", 2000),
        new(2045, "Movies/UHD", 2000),
        new(2050, "Movies/BluRay", 2000),
        new(2060, "Movies/3D", 2000),
        new(2070, "Documentary", 2000),

        // 3000 - Audio
        new(3000, "Audio", null),
        new(3010, "Audio/MP3", 3000),
        new(3020, "Audio/Video", 3000),
        new(3030, "Audio/Audiobook", 3000),
        new(3040, "Audio/Lossless", 3000),
        new(3050, "Audio/Other", 3000),
        new(3060, "Audio/Podcast", 3000),

        // 4000 - PC
        new(4000, "PC", null),
        new(4010, "PC/0day", 4000),
        new(4020, "PC/ISO", 4000),
        new(4030, "PC/Mac", 4000),
        new(4040, "PC/Mobile", 4000),
        new(4050, "PC/Games", 4000),
        new(4060, "PC/Mobile-iOS", 4000),
        new(4070, "PC/Mobile-Android", 4000),

        // 5000 - TV
        new(5000, "TV", null),
        new(5010, "TV/WEB-DL", 5000),
        new(5020, "TV/Foreign", 5000),
        new(5030, "TV/SD", 5000),
        new(5040, "TV/HD", 5000),
        new(5045, "TV/UHD", 5000),
        new(5050, "TV/Other", 5000),
        new(5060, "TV/Sport", 5000),
        new(5070, "TV/Anime", 5000),
        new(5080, "TV/Documentary", 5000),

        // 6000 - XXX
        new(6000, "XXX", null),
        new(6010, "XXX/DVD", 6000),
        new(6020, "XXX/WMV", 6000),
        new(6030, "XXX/XviD", 6000),
        new(6040, "XXX/x264", 6000),
        new(6050, "XXX/Pack", 6000),
        new(6060, "XXX/ImageSet", 6000),
        new(6070, "XXX/Other", 6000),

        // 7000 - Books
        new(7000, "Books", null),
        new(7010, "Books/Magazines", 7000),
        new(7020, "Books/EBook", 7000),
        new(7030, "Books/Comics", 7000),
        new(7040, "Books/Technical", 7000),
        new(7050, "Books/Foreign", 7000),

        // 8000 - Other
        new(8000, "Other", null),
        new(8010, "Other/Misc", 8000),
        new(8020, "Other/Hashed", 8000),
    };

    private static readonly Dictionary<int, StandardCatalogCategory> ById =
        Items.ToDictionary(c => c.Id);

    public static IReadOnlyList<StandardCatalogCategory> GetAllStandard() => Items;

    public static bool IsStandardId(int id) => id is >= 1000 and <= 8999;

    public static bool TryGetStandardName(int id, out string name)
    {
        if (ById.TryGetValue(id, out var item))
        {
            name = item.Name;
            return true;
        }

        name = "";
        return false;
    }

    public static int? GetParentId(int id)
    {
        if (!IsStandardId(id)) return null;
        if (ById.TryGetValue(id, out var item) && item.ParentId.HasValue)
            return item.ParentId.Value;
        if (id % 1000 == 0) return null;
        return (id / 1000) * 1000;
    }
}
