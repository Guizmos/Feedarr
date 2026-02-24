using System.Reflection;
using Feedarr.Api.Dtos.Categories;
using Feedarr.Api.Services.Categories;

namespace Feedarr.Api.Tests;

public sealed class YgegeCapsMergeTests
{
    [Fact]
    public void YgegeCapsMerge_ReturnsFullStandardCatalog_AndAllSpecificCapsIds()
    {
        var standardCapsIds = new[]
        {
            1000, 1030, 1090, 1140, 1180,
            2000, 2020,
            3000, 3020, 3030, 3050,
            4000, 4010, 4020, 4030, 4050, 4070,
            5000, 5050, 5060, 5070, 5080,
            6000, 6060, 6070,
            7000, 7010, 7020, 7030,
            8000
        };

        var specificCapsIds = new[]
        {
            102139,102140,102141,102142,102143,102144,102145,102147,102148,102149,102150,102151,102152,102153,
            102154,102155,102156,102157,102158,102159,102160,102161,102162,102163,102164,102165,102166,102167,
            102168,102169,102170,102171,102172,102173,102174,102175,102176,102177,102178,102179,102180,102181,
            102182,102183,102184,102185,102186,102187,102188,102189,102190,102191,102200,102201,102202,102300,
            102301,102302,102303,102304,102401,102402
        };

        var allCapsIds = standardCapsIds.Concat(specificCapsIds).ToArray();
        var capsById = allCapsIds.ToDictionary(id => id, id => $"Cat {id}");
        var supportedIds = allCapsIds.ToHashSet();

        var method = typeof(CategoryRecommendationService).GetMethod(
            "BuildFlatCategories",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new object[]
        {
            capsById,
            supportedIds,
            true,
            true
        });

        var categories = Assert.IsType<List<CapsCategoryDto>>(result);
        var byId = categories.ToDictionary(c => c.Id, c => c);

        foreach (var id in specificCapsIds)
        {
            Assert.True(byId.ContainsKey(id), $"Specific caps id {id} missing from response.");
        }

        foreach (var std in StandardCategoryCatalog.GetAllStandard())
        {
            Assert.True(byId.ContainsKey(std.Id), $"Standard catalog id {std.Id} missing from response.");
        }

        foreach (var std in StandardCategoryCatalog.GetAllStandard())
        {
            var expectedSupported = supportedIds.Contains(std.Id);
            Assert.Equal(expectedSupported, byId[std.Id].IsSupported);
            Assert.True(byId[std.Id].IsStandard);
        }

        Assert.Equal(
            specificCapsIds.Length,
            categories.Count(c => !c.IsStandard));
        Assert.All(
            categories.Where(c => !c.IsStandard),
            c => Assert.True(c.IsSupported, $"Non-standard id {c.Id} must be supported."));

        var ids = categories.Select(c => c.Id).ToArray();
        var sorted = ids.OrderBy(id => id).ToArray();
        Assert.Equal(sorted, ids);
    }
}
