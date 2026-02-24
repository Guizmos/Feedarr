using Feedarr.Api.Services.Categories;

namespace Feedarr.Api.Tests;

public sealed class CategorySelectionTests
{
    [Fact]
    public void NormalizeSelectedCategoryIds_LeafRemovesParentInSameStandardGroup()
    {
        var normalized = CategorySelection.NormalizeSelectedCategoryIds(new[] { 5000, 5070 });
        Assert.Equal(new[] { 5070 }, normalized.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void NormalizeSelectedCategoryIds_OnlyParent_Remains()
    {
        var normalized = CategorySelection.NormalizeSelectedCategoryIds(new[] { 5000 });
        Assert.Equal(new[] { 5000 }, normalized.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void NormalizeSelectedCategoryIds_MultiGroups_RemovesOnlyParentsWithLeaf()
    {
        var normalized = CategorySelection.NormalizeSelectedCategoryIds(new[] { 5000, 2000, 2020 });
        Assert.Equal(new[] { 2020, 5000 }, normalized.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void MatchesSelectedCategoryIds_LeafSelected_MatchesLeafItem()
    {
        var matches = CategorySelection.MatchesSelectedCategoryIds(
            itemCategoryIds: new[] { 5000, 5070 },
            selectedCategoryIds: new[] { 5070 });
        Assert.True(matches);
    }

    [Fact]
    public void MatchesSelectedCategoryIds_ParentSelected_MatchesLeafItem()
    {
        var matches = CategorySelection.MatchesSelectedCategoryIds(
            itemCategoryIds: new[] { 5070 },
            selectedCategoryIds: new[] { 5000 });
        Assert.True(matches);
    }

    [Fact]
    public void MatchesSelectedCategoryIds_NoSelection_ReturnsFalse()
    {
        var matches = CategorySelection.MatchesSelectedCategoryIds(
            itemCategoryIds: new[] { 5000, 5070 },
            selectedCategoryIds: Array.Empty<int>());
        Assert.False(matches);
    }

    [Fact]
    public void PickBestCategoryId_ParentMapMatchesLeafViaStandardGroup()
    {
        var map = new Dictionary<int, (string key, string label)>
        {
            [5000] = ("series", "Série TV")
        };

        var picked = CategorySelection.PickBestCategoryId(new[] { 5070 }, map);

        Assert.Equal(5000, picked);
    }

    [Fact]
    public void PickBestCategoryId_LeafMapWinsOverParentMap()
    {
        var map = new Dictionary<int, (string key, string label)>
        {
            [5000] = ("series", "Série TV"),
            [5070] = ("anime", "Anime")
        };

        var picked = CategorySelection.PickBestCategoryId(new[] { 5070 }, map);

        Assert.Equal(5070, picked);
    }
}

