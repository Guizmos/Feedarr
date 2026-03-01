using System.Globalization;

namespace Feedarr.Api.Services.Categories;

public static class CategorySelectionAudit
{
    public const string PersistedNull = "PersistedNull";
    public const string PersistedEmpty = "PersistedEmpty";
    public const string ParseError = "ParseError";
    public const string IndexerNotFound = "IndexerNotFound";
    public const string WizardIncomplete = "WizardIncomplete";
    public const string MergeBehavior = "MergeBehavior";
    public const string Other = "Other";

    public static string InferReason(
        bool persistedWasNull,
        int persistedCount,
        int parseErrorCount,
        int mappedCount,
        bool wizardIncomplete = false)
    {
        if (parseErrorCount > 0)
            return ParseError;

        if (persistedWasNull)
            return PersistedNull;

        if (persistedCount <= 0)
            return wizardIncomplete ? WizardIncomplete : PersistedEmpty;

        if (mappedCount > 0 && persistedCount > mappedCount)
            return MergeBehavior;

        return Other;
    }

    public static bool ShouldUseFallback(IReadOnlyCollection<int>? selectedCategoryIds)
        => selectedCategoryIds is null || selectedCategoryIds.Count == 0;

    public static IReadOnlyList<int> ParseRawIds(string? rawIds, out int parseErrorCount)
    {
        parseErrorCount = 0;
        if (string.IsNullOrWhiteSpace(rawIds))
            return Array.Empty<int>();

        var parsed = new List<int>();
        foreach (var token in rawIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0)
            {
                parsed.Add(value);
            }
            else
            {
                parseErrorCount++;
            }
        }

        return parsed
            .Distinct()
            .OrderBy(id => id)
            .ToList();
    }

    public static string SummarizeIds(IEnumerable<int>? ids, int max = 20)
    {
        if (ids is null)
            return "-";

        var list = ids
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        if (list.Count == 0)
            return "-";

        if (list.Count <= max)
            return string.Join(",", list);

        return $"{string.Join(",", list.Take(max))},...(+{list.Count - max})";
    }
}
