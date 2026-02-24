using Feedarr.Api.Models;
using Feedarr.Api.Services.Categories;

namespace Feedarr.Api.Tests;

public sealed class AnimeLabelConsistencyTests
{
    [Fact]
    public void CategoryClassifier_LabelForKey_Anime_IsCanonical()
    {
        Assert.Equal("Anime", CategoryClassifier.LabelForKey("anime"));
    }

    [Fact]
    public void UnifiedCategoryMappings_ToLabel_Anime_IsCanonical()
    {
        Assert.Equal("Anime", UnifiedCategoryMappings.ToLabel(UnifiedCategory.Anime));
    }
}
