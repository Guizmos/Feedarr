using Feedarr.Api.Data;
using Feedarr.Api.Services.Categories;

namespace Feedarr.Api.Tests;

public sealed class CategoryClassifierTests
{
    // ─── ClassifyById ─────────────────────────────────────────────────────────

    [Fact]
    public void ClassifyById_5070_ReturnsSeries()
    {
        // Standard-only : 5070 ∈ range 5000–5999 → "series" (pas d'override "anime")
        var result = CategoryClassifier.ClassifyById(5070, new HashSet<string>());
        Assert.Equal("series", result);
    }

    [Fact]
    public void ClassifyById_5000_ReturnsSeries()
    {
        var result = CategoryClassifier.ClassifyById(5000, new HashSet<string>());
        Assert.Equal("series", result);
    }

    [Fact]
    public void ClassifyById_2000_ReturnsFilms()
    {
        var result = CategoryClassifier.ClassifyById(2000, new HashSet<string>());
        Assert.Equal("films", result);
    }

    [Fact]
    public void ClassifyById_3000_ReturnsAudio()
    {
        var result = CategoryClassifier.ClassifyById(3000, new HashSet<string>());
        Assert.Equal("audio", result);
    }

    [Fact]
    public void ClassifyById_7000_ReturnsBooks()
    {
        var result = CategoryClassifier.ClassifyById(7000, new HashSet<string>());
        Assert.Equal("books", result);
    }

    [Fact]
    public void ClassifyById_7030_ReturnsBooks()
    {
        // Standard-only : 7030 ∈ range 7000–7999 → "books" (pas d'override "comics")
        var result = CategoryClassifier.ClassifyById(7030, new HashSet<string>());
        Assert.Equal("books", result);
    }

    [Fact]
    public void ClassifyById_7035_ReturnsBooks()
    {
        var result = CategoryClassifier.ClassifyById(7035, new HashSet<string>());
        Assert.Equal("books", result);
    }

    [Fact]
    public void ClassifyById_7039_ReturnsBooks()
    {
        var result = CategoryClassifier.ClassifyById(7039, new HashSet<string>());
        Assert.Equal("books", result);
    }

    [Fact]
    public void ClassifyById_7040_ReturnsBooks()
    {
        var result = CategoryClassifier.ClassifyById(7040, new HashSet<string>());
        Assert.Equal("books", result);
    }

    [Fact]
    public void ClassifyById_4050_ReturnsGames()
    {
        var result = CategoryClassifier.ClassifyById(4050, new HashSet<string>());
        Assert.Equal("games", result);
    }

    [Fact]
    public void ClassifyById_9999_ReturnsNull()
    {
        // ID hors plages connues → null (pas de classification)
        var result = CategoryClassifier.ClassifyById(9999, new HashSet<string>());
        Assert.Null(result);
    }

    // Nouveaux ranges délégués à StandardCategoryGrouping (sans tokens)

    [Fact]
    public void ClassifyById_1000_ReturnsGames()
    {
        var result = CategoryClassifier.ClassifyById(1000, new HashSet<string>());
        Assert.Equal("games", result);
    }

    [Fact]
    public void ClassifyById_1180_ReturnsGames()
    {
        var result = CategoryClassifier.ClassifyById(1180, new HashSet<string>());
        Assert.Equal("games", result);
    }

    [Fact]
    public void ClassifyById_4000_ReturnsGames_NoTokensRequired()
    {
        // 4000–4999 retourne "games" sans tokens (token-free depuis StandardCategoryGrouping)
        var result = CategoryClassifier.ClassifyById(4000, new HashSet<string>());
        Assert.Equal("games", result);
    }

    [Fact]
    public void ClassifyById_6000_ReturnsXxx()
    {
        var result = CategoryClassifier.ClassifyById(6000, new HashSet<string>());
        Assert.Equal("xxx", result);
    }

    [Fact]
    public void ClassifyById_6100_ReturnsXxx()
    {
        var result = CategoryClassifier.ClassifyById(6100, new HashSet<string>());
        Assert.Equal("xxx", result);
    }

    [Fact]
    public void ClassifyById_8000_ReturnsOther()
    {
        var result = CategoryClassifier.ClassifyById(8000, new HashSet<string>());
        Assert.Equal("other", result);
    }

    [Fact]
    public void ClassifyById_8500_ReturnsOther()
    {
        var result = CategoryClassifier.ClassifyById(8500, new HashSet<string>());
        Assert.Equal("other", result);
    }

    // ─── ClassifyByTokens ─────────────────────────────────────────────────────

    [Fact]
    public void ClassifyByTokens_AnimeToken_ReturnsAnime()
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "anime" };
        var result = CategoryClassifier.ClassifyByTokens(tokens);
        Assert.Equal("anime", result);
    }

    [Fact]
    public void ClassifyByTokens_SeriesToken_ReturnsSeries()
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "serie" };
        var result = CategoryClassifier.ClassifyByTokens(tokens);
        Assert.Equal("series", result);
    }

    [Fact]
    public void ClassifyByTokens_BlacklistedToken_ReturnsNull()
    {
        // "porn" est dans BlacklistTokens → null
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "serie", "porn" };
        var result = CategoryClassifier.ClassifyByTokens(tokens);
        Assert.Null(result);
    }

    [Fact]
    public void ClassifyByTokens_EmptyTokens_ReturnsNull()
    {
        var result = CategoryClassifier.ClassifyByTokens(new HashSet<string>());
        Assert.Null(result);
    }

    // ─── SqlChunkHelper ───────────────────────────────────────────────────────

    [Fact]
    public void Chunk_EmptySource_NoChunks()
    {
        var result = SqlChunkHelper.Chunk(Array.Empty<int>(), 500).ToList();
        Assert.Empty(result);
    }

    [Fact]
    public void Chunk_SingleItem_OneChunk()
    {
        var result = SqlChunkHelper.Chunk(new[] { 1 }, 500).ToList();
        Assert.Single(result);
        Assert.Equal(1, result[0][0]);
    }

    [Fact]
    public void Chunk_ExactDivision_CorrectChunkCount()
    {
        var source = Enumerable.Range(1, 1000).ToList();
        var chunks = SqlChunkHelper.Chunk(source, 500).ToList();
        Assert.Equal(2, chunks.Count);
        Assert.Equal(500, chunks[0].Count);
        Assert.Equal(500, chunks[1].Count);
    }

    [Fact]
    public void Chunk_RemainingItems_LastChunkSmaller()
    {
        var source = Enumerable.Range(1, 501).ToList();
        var chunks = SqlChunkHelper.Chunk(source, 500).ToList();
        Assert.Equal(2, chunks.Count);
        Assert.Equal(500, chunks[0].Count);
        Assert.Single(chunks[1]);
    }

    [Fact]
    public void Chunk_AllItemsPreserved()
    {
        var source = Enumerable.Range(1, 1234).ToList();
        var chunks = SqlChunkHelper.Chunk(source, 500).ToList();
        var all = chunks.SelectMany(c => c).ToList();
        Assert.Equal(source.Count, all.Count);
        Assert.Equal(source, all);
    }

    [Fact]
    public void Chunk_InvalidChunkSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SqlChunkHelper.Chunk(new[] { 1 }, 0).ToList());
    }
}
