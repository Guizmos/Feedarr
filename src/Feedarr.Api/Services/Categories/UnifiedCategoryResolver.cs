using Feedarr.Api.Models;

namespace Feedarr.Api.Services.Categories;

public sealed class UnifiedCategoryResolver
{
    private static readonly Dictionary<string, Dictionary<int, UnifiedCategory>> SpecMappings =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["C411"] = new Dictionary<int, UnifiedCategory>
            {
                [102000] = UnifiedCategory.Film,
                [105000] = UnifiedCategory.Serie,
                [105080] = UnifiedCategory.Emission
            },
            ["YGEGE"] = new Dictionary<int, UnifiedCategory>
            {
                [102183] = UnifiedCategory.Film,
                [102178] = UnifiedCategory.Animation,
                [102184] = UnifiedCategory.Serie,
                [102185] = UnifiedCategory.Spectacle,
                [102182] = UnifiedCategory.Emission,
                [102161] = UnifiedCategory.JeuWindows
            },
            ["LACALE"] = new Dictionary<int, UnifiedCategory>
            {
                [131681] = UnifiedCategory.Film,
                [117804] = UnifiedCategory.Serie
            },
            ["TOS"] = new Dictionary<int, UnifiedCategory>
            {
                [100001] = UnifiedCategory.Film,
                [100002] = UnifiedCategory.Serie
            }
        };

    public UnifiedCategory Resolve(
        string? indexerKey,
        int? stdCategoryId,
        int? specCategoryId,
        IReadOnlyCollection<int>? allCategoryIds)
    {
        var normalizedKey = NormalizeIndexerKey(indexerKey);
        var (stdId, specId) = ResolveStdSpec(stdCategoryId, specCategoryId, allCategoryIds);

        if (specId.HasValue)
        {
            if (!SpecMappings.TryGetValue(normalizedKey, out var specMap))
            {
                var fallbackKey = SpecMappings.Keys
                    .FirstOrDefault(k => normalizedKey.Contains(k, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(fallbackKey))
                    specMap = SpecMappings[fallbackKey];
            }

            if (specMap is not null && specMap.TryGetValue(specId.Value, out var mapped))
            {
                return mapped;
            }
        }

        if (stdId.HasValue)
        {
            if (stdId.Value == 4050 && normalizedKey == "LACALE")
                return UnifiedCategory.JeuWindows;

            if (stdId.Value >= 2000 && stdId.Value <= 2999)
                return UnifiedCategory.Film;

            if (stdId.Value >= 3000 && stdId.Value <= 3999)
                return UnifiedCategory.Audio;

            if (stdId.Value >= 5000 && stdId.Value <= 5999)
                return UnifiedCategory.Serie;

            if (stdId.Value >= 7000 && stdId.Value <= 7999)
            {
                if (specId is >= 7030 and < 7040)
                    return UnifiedCategory.Comic;
                return UnifiedCategory.Book;
            }

            if (stdId.Value == 4050)
                return UnifiedCategory.Autre;
        }

        return UnifiedCategory.Autre;
    }

    public static (int? stdId, int? specId) ResolveStdSpec(
        int? stdCategoryId,
        int? specCategoryId,
        IReadOnlyCollection<int>? allCategoryIds)
    {
        var stdId = stdCategoryId;
        var specId = specCategoryId;

        if ((stdId.HasValue && specId.HasValue) || allCategoryIds is null || allCategoryIds.Count == 0)
            return (stdId, specId);

        foreach (var id in allCategoryIds)
        {
            if (!specId.HasValue && id >= 10000)
                specId = id;
            else if (!stdId.HasValue && id >= 1000 && id <= 8999)
                stdId = id;

            if (stdId.HasValue && specId.HasValue)
                break;
        }

        return (stdId, specId);
    }

    private static string NormalizeIndexerKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";
        var normalized = key.Normalize(System.Text.NormalizationForm.FormD);
        var chars = normalized
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Where(char.IsLetterOrDigit)
            .ToArray();
        return new string(chars).ToUpperInvariant();
    }
}
