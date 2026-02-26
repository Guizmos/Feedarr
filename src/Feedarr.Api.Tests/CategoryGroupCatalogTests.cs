using Feedarr.Api.Services.Categories;

namespace Feedarr.Api.Tests;

public sealed class CategoryGroupCatalogTests
{
    [Fact]
    public void CanonicalKeys_AreExactlyExpectedTenKeys()
    {
        var expected = new[]
        {
            "films", "series", "animation", "anime", "games",
            "comics", "books", "audio", "spectacle", "emissions"
        };

        var actual = CategoryGroupCatalog.CanonicalKeys.OrderBy(x => x).ToArray();
        Assert.Equal(expected.OrderBy(x => x).ToArray(), actual);
    }

    [Theory]
    [InlineData("films", "films")]
    [InlineData("film", "films")]
    [InlineData("movies", "films")]
    [InlineData("serie", "series")]
    [InlineData("tv", "series")]
    [InlineData("show", "emissions")]
    [InlineData("shows", "emissions")]
    [InlineData("emission", "emissions")]
    [InlineData("games", "games")]
    [InlineData("game", "games")]
    [InlineData("books", "books")]
    [InlineData("comic", "comics")]
    public void TryNormalizeKey_AcceptsCanonicalAndAliases(string raw, string expectedCanonical)
    {
        var ok = CategoryGroupCatalog.TryNormalizeKey(raw, out var canonicalKey);
        Assert.True(ok);
        Assert.Equal(expectedCanonical, canonicalKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("other")]
    [InlineData("unknown")]
    public void TryNormalizeKey_RejectsUnknownAndOther(string? raw)
    {
        var ok = CategoryGroupCatalog.TryNormalizeKey(raw, out var canonicalKey);
        Assert.False(ok);
        Assert.Equal("", canonicalKey);
    }

    [Fact]
    public void LabelForKey_ReturnsCanonicalLabel()
    {
        Assert.Equal("Films", CategoryGroupCatalog.LabelForKey("films"));
        Assert.Equal("Série TV", CategoryGroupCatalog.LabelForKey("series"));
        Assert.Equal("Jeux PC", CategoryGroupCatalog.LabelForKey("games"));
    }

    [Fact]
    public void AssertCanonicalKey_ThrowsForAlias()
    {
        var ex = Assert.Throws<ArgumentException>(() => CategoryGroupCatalog.AssertCanonicalKey("shows"));
        Assert.Contains("not canonical", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
