using System.Globalization;
using System.Text;

namespace Feedarr.Api.Services.Categories;

public static class CategoryGroupCatalog
{
    private static readonly IReadOnlyDictionary<string, string> LabelsByCanonicalKey =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["films"] = "Films",
            ["series"] = "Série TV",
            ["animation"] = "Animation",
            ["anime"] = "Anime",
            ["games"] = "Jeux PC",
            ["comics"] = "Comics",
            ["books"] = "Livres",
            ["audio"] = "Audio",
            ["spectacle"] = "Spectacle",
            ["emissions"] = "Emissions"
        };

    private static readonly IReadOnlyDictionary<string, string> AliasesToCanonicalKey =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["shows"] = "emissions",
            ["show"] = "emissions",
            ["emission"] = "emissions",
            ["emissions"] = "emissions",
            ["tv"] = "series",
            ["serie"] = "series",
            ["series"] = "series",
            ["movie"] = "films",
            ["movies"] = "films",
            ["film"] = "films",
            ["films"] = "films",
            ["game"] = "games",
            ["games"] = "games",
            ["book"] = "books",
            ["books"] = "books",
            ["comic"] = "comics",
            ["comics"] = "comics"
        };

    public static IReadOnlySet<string> CanonicalKeys { get; } =
        new HashSet<string>(LabelsByCanonicalKey.Keys, StringComparer.Ordinal);

    public static bool TryNormalizeKey(string? raw, out string canonicalKey)
    {
        canonicalKey = "";
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var token = NormalizeToken(raw);
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (string.Equals(token, "other", StringComparison.Ordinal))
            return false;

        if (CanonicalKeys.Contains(token))
        {
            canonicalKey = token;
            return true;
        }

        if (AliasesToCanonicalKey.TryGetValue(token, out var mapped))
        {
            canonicalKey = mapped;
            return true;
        }

        return false;
    }

    public static string LabelForKey(string canonicalKey)
    {
        AssertCanonicalKey(canonicalKey);
        return LabelsByCanonicalKey[canonicalKey];
    }

    public static void AssertCanonicalKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Category group key is required.", nameof(key));

        if (!CanonicalKeys.Contains(key))
            throw new ArgumentException($"Category group key '{key}' is not canonical.", nameof(key));
    }

    private static string NormalizeToken(string value)
    {
        var trimmed = value.Trim().ToLowerInvariant();
        var normalized = trimmed.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                sb.Append(ch);
                continue;
            }

            if (ch is '-' or '_' or ' ')
                sb.Append(' ');
        }

        return sb.ToString().Replace(" ", "", StringComparison.Ordinal);
    }
}
