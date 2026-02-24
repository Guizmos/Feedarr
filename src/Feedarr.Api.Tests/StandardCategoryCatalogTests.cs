using Feedarr.Api.Services.Categories;

namespace Feedarr.Api.Tests;

public sealed class StandardCategoryCatalogTests
{
    [Fact]
    public void Catalog_ContainsExpectedTvStandardIds()
    {
        var ids = StandardCategoryCatalog.GetAllStandard().Select(c => c.Id).ToHashSet();

        Assert.Contains(5000, ids);
        Assert.Contains(5050, ids);
        Assert.Contains(5060, ids);
        Assert.Contains(5070, ids);
        Assert.Contains(5080, ids);
    }

    [Fact]
    public void StandardHelpers_WorkForStandardIds()
    {
        Assert.True(StandardCategoryCatalog.IsStandardId(5000));
        Assert.True(StandardCategoryCatalog.IsStandardId(5070));
        Assert.False(StandardCategoryCatalog.IsStandardId(120001));
        Assert.Equal(5000, StandardCategoryCatalog.GetParentId(5070));
        Assert.Null(StandardCategoryCatalog.GetParentId(5000));
        Assert.Null(StandardCategoryCatalog.GetParentId(120001));
    }
}
