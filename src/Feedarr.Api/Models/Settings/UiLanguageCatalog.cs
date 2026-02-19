namespace Feedarr.Api.Models.Settings;

public static class UiLanguageCatalog
{
    public const string DefaultUiLanguage = "fr-FR";
    public const string DefaultMediaInfoLanguage = "fr-FR";

    private static readonly string[] SupportedLanguages =
    [
        "fr-FR",
        "en-US"
    ];

    private static readonly HashSet<string> SupportedLanguageSet = new(
        SupportedLanguages,
        StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> Iso639ByLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fr-FR"] = "fr",
        ["en-US"] = "en"
    };

    // Keep French/English high priority while still honoring user preference first.
    private static readonly string[] PosterFallbackOrder =
    [
        "fr",
        "en"
    ];

    public static string NormalizeUiLanguage(string? raw)
        => Normalize(raw, DefaultUiLanguage);

    public static string NormalizeMediaInfoLanguage(string? raw)
        => Normalize(raw, DefaultMediaInfoLanguage);

    public static string GetMediaInfoIso639_1(string? languageTag)
    {
        var normalized = NormalizeMediaInfoLanguage(languageTag);
        return Iso639ByLanguage.TryGetValue(normalized, out var iso) ? iso : "fr";
    }

    public static IReadOnlyList<string> BuildPosterLanguagePriority(string? mediaInfoLanguage)
    {
        var preferred = GetMediaInfoIso639_1(mediaInfoLanguage);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();

        AddIfNew(preferred);

        foreach (var fallback in PosterFallbackOrder)
            AddIfNew(fallback);

        AddIfNew("null");
        return list;

        void AddIfNew(string value)
        {
            if (seen.Add(value))
                list.Add(value);
        }
    }

    private static string Normalize(string? raw, string fallback)
    {
        var value = (raw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return SupportedLanguageSet.Contains(value) ? value : fallback;
    }
}
