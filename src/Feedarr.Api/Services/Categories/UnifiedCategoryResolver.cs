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

        // Cas spécial Comics : stdId enfant dans 7030-7039
        // Ex: [7000,7035] → NormalizeCategoryIds supprime parent 7000 → stdId=7035
        if (stdId is >= 7030 and < 7040)
            return UnifiedCategory.Comic;

        // Compat ancienne forme : stdId parent du groupe livres + specId Comics
        if (stdId is >= 7000 and <= 7999 && specId is >= 7030 and < 7040)
            return UnifiedCategory.Comic;

        // Résoudre stdId via méthode dédiée (5070 → Anime, etc.)
        UnifiedCategory? fromStd = stdId.HasValue ? ResolveStdId(stdId.Value, normalizedKey) : null;

        // Résoudre specId via SpecMappings indexeur
        UnifiedCategory? fromSpec = null;
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
                fromSpec = mapped;
        }

        // Le plus spécifique gagne (ex: Anime > Série même si specId dit Série)
        if (fromStd.HasValue && fromSpec.HasValue)
            return Specificity(fromStd.Value) >= Specificity(fromSpec.Value) ? fromStd.Value : fromSpec.Value;
        if (fromStd.HasValue) return fromStd.Value;
        if (fromSpec.HasValue) return fromSpec.Value;
        return UnifiedCategory.Autre;
    }

    public static (int? stdId, int? specId) ResolveStdSpec(
        int? stdCategoryId,
        int? specCategoryId,
        IReadOnlyCollection<int>? allCategoryIds)
    {
        var specId = specCategoryId;
        // Classer le stdCategoryId entrant comme parent ou enfant
        int? parentStdId = stdCategoryId.HasValue && stdCategoryId.Value % 1000 == 0 ? stdCategoryId : null;
        int? childStdId  = stdCategoryId.HasValue && stdCategoryId.Value % 1000 != 0 ? stdCategoryId : null;

        // Toujours scanner allCategoryIds pour trouver un enfant plus spécifique
        if (allCategoryIds is not null && allCategoryIds.Count > 0)
        {
            foreach (var id in allCategoryIds)
            {
                if (id >= 10000)
                {
                    specId ??= id;
                }
                else if (id >= 1000 && id <= 8999)
                {
                    if (id % 1000 == 0)
                        parentStdId ??= id;
                    else
                        childStdId ??= id; // enfant plus spécifique
                }
            }
        }

        // L'enfant (ex: 5070) prend priorité sur le parent (ex: 5000)
        return (childStdId ?? parentStdId, specId);
    }

    private static UnifiedCategory ResolveStdId(int id, string normalizedKey)
    {
        if (id == 5070) return UnifiedCategory.Anime;
        if (id == 4050) return normalizedKey == "LACALE" ? UnifiedCategory.JeuWindows : UnifiedCategory.Autre;
        if (id >= 2000 && id <= 2999) return UnifiedCategory.Film;
        if (id >= 3000 && id <= 3999) return UnifiedCategory.Audio;
        if (id >= 5000 && id <= 5999) return UnifiedCategory.Serie;
        if (id >= 7000 && id <= 7999) return UnifiedCategory.Book;
        return UnifiedCategory.Autre;
    }

    private static int Specificity(UnifiedCategory cat) => cat switch
    {
        UnifiedCategory.Anime      => 10,
        UnifiedCategory.Comic      => 9,
        UnifiedCategory.Spectacle  => 8,
        UnifiedCategory.Animation  => 7,
        UnifiedCategory.Serie      => 6,
        UnifiedCategory.Film       => 5,
        UnifiedCategory.Audio      => 4,
        UnifiedCategory.Book       => 3,
        UnifiedCategory.JeuWindows => 2,
        _                          => 0
    };

    /// <summary>
    /// Si le stdCategoryId enfant (ex: 5070→Anime) est plus spécifique que
    /// la catégorie trouvée via source_categories (ex: 105000→Serie), retourne fromStd.
    /// Sinon retourne fromMap inchangé.
    /// Cas : ApplyStdOverride(Serie, 5070) → Anime
    /// </summary>
    public static UnifiedCategory ApplyStdOverride(UnifiedCategory fromMap, int? stdCategoryId)
    {
        if (!stdCategoryId.HasValue) return fromMap;
        var fromStd = ResolveStdId(stdCategoryId.Value, "");
        if (fromStd == UnifiedCategory.Autre) return fromMap;
        return Specificity(fromStd) > Specificity(fromMap) ? fromStd : fromMap;
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
