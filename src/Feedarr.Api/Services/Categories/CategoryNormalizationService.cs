namespace Feedarr.Api.Services.Categories;

public static class CategoryNormalizationService
{
    /// <summary>
    /// Supprime les IDs parents (multiples de 1000) quand un enfant du même groupe est présent.
    /// Ex: [5000, 5070, 105000] → [5070, 105000]
    /// </summary>
    public static IReadOnlyList<int> NormalizeCategoryIds(IReadOnlyCollection<int> ids)
    {
        var stdIds = ids.Where(id => id >= 1000 && id <= 8999).ToList();
        var toRemove = new HashSet<int>();

        foreach (var id in stdIds)
        {
            if (id % 1000 != 0)
                continue;

            // id est un parent (ex: 5000) — supprimer si un enfant du même groupe existe
            var hasChild = stdIds.Any(c => c != id && c / 1000 * 1000 == id && c % 1000 != 0);
            if (hasChild)
                toRemove.Add(id);
        }

        return toRemove.Count == 0
            ? ids.ToList()
            : ids.Where(id => !toRemove.Contains(id)).ToList();
    }
}
