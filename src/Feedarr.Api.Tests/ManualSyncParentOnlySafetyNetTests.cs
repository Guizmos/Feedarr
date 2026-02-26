using Feedarr.Api.Services.Categories;

namespace Feedarr.Api.Tests;

public sealed class ManualSyncParentOnlySafetyNetTests
{
    [Fact]
    public void ParentAndLeafSelected_NormalizedSelectionIsDeterministic()
    {
        var normalized = CategorySelection.NormalizeSelectedCategoryIds(new[] { 5000, 5050 });

        Assert.Equal(new[] { 5050 }, normalized.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void ParentOnlySelection_AllowsLeafFromSameStandardGroup()
    {
        var matches = CategorySelection.MatchesSelectedCategoryIds(
            itemCategoryIds: new[] { 5070 },
            selectedCategoryIds: new[] { 5000 });

        Assert.True(matches);
    }
}
