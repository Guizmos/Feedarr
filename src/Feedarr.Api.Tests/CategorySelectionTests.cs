using Feedarr.Api.Services.Categories;

namespace Feedarr.Api.Tests;

public sealed class CategorySelectionTests
{
    [Fact]
    public void PickSelectedFallbackCategoryId_SelectedCategoryPresent_ReturnsSelectedCategory()
    {
        var ids = new[] { 3010, 103010 };
        var selectedCategoryIds = new[] { 3010 };

        var picked = CategorySelection.PickSelectedFallbackCategoryId(ids, selectedCategoryIds);

        Assert.Equal(3010, picked);
    }

    [Fact]
    public void PickSelectedFallbackCategoryId_NoSelectedCategories_ReturnsNull()
    {
        var ids = new[] { 3010 };
        var selectedCategoryIds = Array.Empty<int>();

        var picked = CategorySelection.PickSelectedFallbackCategoryId(ids, selectedCategoryIds);

        Assert.Null(picked);
    }
}

