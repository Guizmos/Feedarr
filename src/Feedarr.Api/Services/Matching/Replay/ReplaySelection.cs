namespace Feedarr.Api.Services.Matching.Replay;

public sealed record ReplaySelection(string Method = "sample", IReadOnlyList<string>? Categories = null,
    IReadOnlyList<long>? ReleaseIds = null, string Filter = "all", bool FairBudget = false);
public sealed record ReplayCategoryBudget(string Category, int Selected, int MinimumRequests, int? Limit, int Used);
public sealed record ReplayPreview(long Generation, ReplaySelection Selection, IReadOnlyList<long> SelectedIds,
    IReadOnlyList<ReplayDistribution> Distribution, int MinimumRequests, int Budget, bool PaginationUnknown,
    bool MinimumExceedsBudget, IReadOnlyList<ReplayCategoryBudget> CategoryBudgets);

internal static class ReplaySelectionRules
{
    internal static readonly string[] Categories = ["animation", "emissions", "films", "series", "spectacle"];
    internal static ReplayRequest Normalize(ReplayRequest request)
    {
        if (request.Count is not (10 or 25 or 50 or 100 or 200) || request.Mode is not ("offline" or "tmdb") || request.Budget is < 1 or > 30 || request.Generation < 0)
            throw new ArgumentException("replay_request_invalid");
        if (request.ExpectedIds is { } expected && (expected.Count > 200 || expected.Any(id => id is < 1 or > 9007199254740991) || expected.Distinct().Count() != expected.Count))
            throw new ArgumentException("replay_expected_ids_invalid");
        var s = request.Selection ?? new();
        var categories = (s.Categories ?? Categories).ToArray();
        var ids = (s.ReleaseIds ?? Array.Empty<long>()).ToArray();
        if (s.Method is not ("sample" or "ids" or "shadow_divergences") || s.Filter is not ("all" or "without_poster" or "without_identity" or "with_identity") ||
            categories.Length is < 1 or > 5 || categories.Any(c => !Categories.Contains(c, StringComparer.Ordinal)) || categories.Distinct().Count() != categories.Length ||
            ids.Length > 200 || ids.Any(id => id is < 1 or > 9007199254740991) || ids.Distinct().Count() != ids.Length ||
            (s.Method == "ids" ? ids.Length == 0 : ids.Length != 0)) throw new ArgumentException("replay_selection_invalid");
        return request with { ExpectedIds = request.ExpectedIds is null ? null : Array.AsReadOnly(request.ExpectedIds.ToArray()), Selection = s with { Categories = Array.AsReadOnly(categories.Order(StringComparer.Ordinal).ToArray()), ReleaseIds = Array.AsReadOnly(ids) } };
    }
    internal static IReadOnlyList<ReplayCategoryBudget> Allocate(IReadOnlyList<ReplayDistribution> distribution,
        IReadOnlyDictionary<string, int> minimum, int budget, bool fair)
    {
        var groups = distribution.OrderBy(g => g.Category, StringComparer.Ordinal).ToArray();
        return groups.Select((g, i) => new ReplayCategoryBudget(g.Category, g.Count, minimum.GetValueOrDefault(g.Category),
            fair ? budget / groups.Length + (i < budget % groups.Length ? 1 : 0) : null, 0)).ToArray();
    }
}
