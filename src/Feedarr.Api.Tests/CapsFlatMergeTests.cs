using System.Reflection;
using Feedarr.Api.Dtos.Categories;
using Feedarr.Api.Services.Categories;

namespace Feedarr.Api.Tests;

public sealed class CapsFlatMergeTests
{
    private static List<CapsCategoryDto> BuildFlatCategories(
        IDictionary<int, string> capsById,
        IEnumerable<int> supportedIds,
        IDictionary<int, (string key, string label)>? assignedById = null,
        IEnumerable<int>? selectedIds = null,
        bool includeStandardCatalog = true,
        bool includeSpecific = true)
    {
        var method = typeof(CategoryRecommendationService).GetMethod(
            "BuildFlatCategories",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new object[]
        {
            capsById,
            supportedIds.ToHashSet(),
            assignedById ?? new Dictionary<int, (string key, string label)>(),
            (selectedIds ?? Enumerable.Empty<int>()).ToHashSet(),
            includeStandardCatalog,
            includeSpecific
        });

        return Assert.IsType<List<CapsCategoryDto>>(result);
    }

    [Fact]
    public void StandardCatalogAlwaysReturnedWhenIncludeStandardCatalog()
    {
        var capsById = new Dictionary<int, string>
        {
            [2020] = "Movies/Other",
            [5050] = "TV/Other",
        };

        var result = BuildFlatCategories(capsById, new[] { 2020, 5050 });
        var byId = result.ToDictionary(c => c.Id, c => c);

        Assert.True(byId.ContainsKey(2020));
        Assert.True(byId.ContainsKey(5050));
        Assert.True(byId.ContainsKey(3010));
        Assert.True(byId.ContainsKey(4050));
        Assert.True(byId.ContainsKey(7010));
        Assert.True(byId.ContainsKey(6010));
        Assert.True(byId.ContainsKey(8010));

        Assert.True(byId[2020].IsSupported);
        Assert.True(byId[5050].IsSupported);
        Assert.False(byId[3010].IsSupported);
        Assert.False(byId[4050].IsSupported);
    }

    [Fact]
    public void SpecificCategoriesFromCapsRemainInOutputAsSupported()
    {
        var capsById = new Dictionary<int, string>
        {
            [5000] = "TV",
            [120001] = "Movies/Fansub",
        };

        var result = BuildFlatCategories(capsById, new[] { 5000, 120001 });
        var specific = result.Single(c => c.Id == 120001);

        Assert.False(specific.IsStandard);
        Assert.True(specific.IsSupported);
    }

    [Fact]
    public void OutputHasNoRecommendationOrTypesContract()
    {
        var hasTypes = typeof(CapsCategoriesResponseDto).GetProperty("Types");
        var hasRecommended = typeof(CapsCategoryDto).GetProperty("IsRecommended");

        Assert.Null(hasTypes);
        Assert.Null(hasRecommended);
    }

    [Fact]
    public void AssignedMappingIsMergedIntoCapsOutput()
    {
        var capsById = new Dictionary<int, string>
        {
            [5050] = "TV/Other",
            [120001] = "Anime/Raw"
        };

        var assigned = new Dictionary<int, (string key, string label)>
        {
            [5050] = ("series", "Série TV"),
            [120001] = ("anime", "Anime")
        };

        var result = BuildFlatCategories(
            capsById,
            new[] { 5050, 120001 },
            assignedById: assigned,
            selectedIds: new[] { 5050, 120001 });
        var byId = result.ToDictionary(c => c.Id);

        Assert.True(byId[5050].IsAssigned);
        Assert.Equal("series", byId[5050].AssignedGroupKey);
        Assert.Equal("Série TV", byId[5050].AssignedGroupLabel);

        Assert.True(byId[120001].IsAssigned);
        Assert.Equal("anime", byId[120001].AssignedGroupKey);
        Assert.Equal("Anime", byId[120001].AssignedGroupLabel);
    }
}
