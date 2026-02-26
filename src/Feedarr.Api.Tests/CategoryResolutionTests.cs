using Feedarr.Api.Models;
using Feedarr.Api.Services.Categories;

namespace Feedarr.Api.Tests;

public sealed class CategoryResolutionTests
{
    // ─── ResolveStdSpec ───────────────────────────────────────────────────────

    [Fact]
    public void ResolveStdSpec_ChildBeatsParent_ReturnsChild()
    {
        // [5000 (parent), 5070 (enfant anime)] → stdId doit être 5070
        var (stdId, _) = UnifiedCategoryResolver.ResolveStdSpec(null, null, new[] { 5000, 5070 });
        Assert.Equal(5070, stdId);
    }

    [Fact]
    public void ResolveStdSpec_ParentOnly_ReturnsParent()
    {
        var (stdId, _) = UnifiedCategoryResolver.ResolveStdSpec(null, null, new[] { 5000 });
        Assert.Equal(5000, stdId);
    }

    [Fact]
    public void ResolveStdSpec_ExistingParentStdId_OverriddenByChildInAllIds()
    {
        // stdCategoryId=5000 déjà connu, mais allCategoryIds contient 5070
        var (stdId, _) = UnifiedCategoryResolver.ResolveStdSpec(5000, 105000, new[] { 5000, 105000, 5070 });
        Assert.Equal(5070, stdId);
    }

    [Fact]
    public void ResolveStdSpec_SpecIdExtracted()
    {
        var (_, specId) = UnifiedCategoryResolver.ResolveStdSpec(null, null, new[] { 5000, 105000, 5070 });
        Assert.Equal(105000, specId);
    }

    // ─── UnifiedCategoryResolver.Resolve ─────────────────────────────────────

    [Fact]
    public void Resolve_Triplet_5000_105000_5070_ReturnsAnime()
    {
        // Bug principal : les 157 releases Torr9 classées en Série au lieu de Anime
        var resolver = new UnifiedCategoryResolver();
        var result = resolver.Resolve("Torr9", null, null, new[] { 5000, 105000, 5070 });
        Assert.Equal(UnifiedCategory.Anime, result);
    }

    [Fact]
    public void Resolve_AnimeChildBeatsSerieSpec_ReturnsAnime()
    {
        // Même scénario avec stdId=5000 déjà connu (cas reprocessing depuis DB)
        var resolver = new UnifiedCategoryResolver();
        var result = resolver.Resolve("Torr9", 5000, 105000, new[] { 5000, 105000, 5070 });
        Assert.Equal(UnifiedCategory.Anime, result);
    }

    [Fact]
    public void Resolve_StdId5000_NoChild_ReturnsSerie()
    {
        // Série normale sans sous-catégorie anime
        var resolver = new UnifiedCategoryResolver();
        var result = resolver.Resolve("Torr9", null, null, new[] { 5000, 105000 });
        Assert.Equal(UnifiedCategory.Serie, result);
    }

    [Fact]
    public void Resolve_C411_105000_ReturnsSerie()
    {
        // C411 avec specId 105000 → Série (via SpecMappings C411)
        var resolver = new UnifiedCategoryResolver();
        var result = resolver.Resolve("C411", null, 105000, new[] { 5000, 105000 });
        Assert.Equal(UnifiedCategory.Serie, result);
    }

    // ─── CategoryNormalizationService ─────────────────────────────────────────

    [Fact]
    public void NormalizeCategoryIds_RemovesParentWhenChildPresent()
    {
        var result = CategoryNormalizationService.NormalizeCategoryIds(new[] { 5000, 5070, 105000 });
        Assert.DoesNotContain(5000, result);
        Assert.Contains(5070, result);
        Assert.Contains(105000, result);
    }

    [Fact]
    public void NormalizeCategoryIds_KeepsParentWhenNoChild()
    {
        var result = CategoryNormalizationService.NormalizeCategoryIds(new[] { 5000, 105000 });
        Assert.Contains(5000, result);
        Assert.Contains(105000, result);
    }

    [Fact]
    public void NormalizeCategoryIds_NoChange_WhenOnlyChildren()
    {
        var result = CategoryNormalizationService.NormalizeCategoryIds(new[] { 5070, 105000 });
        Assert.Contains(5070, result);
        Assert.Contains(105000, result);
        Assert.Equal(2, result.Count);
    }

    // ─── ApplyStdOverride ─────────────────────────────────────────────────────

    [Fact]
    public void ApplyStdOverride_5070BeatsSerieFromMap_ReturnsAnime()
    {
        // Cas principal : 5070→Anime bat Serie venu de source_categories (105000→series)
        var result = UnifiedCategoryResolver.ApplyStdOverride(UnifiedCategory.Serie, 5070);
        Assert.Equal(UnifiedCategory.Anime, result);
    }

    [Fact]
    public void ApplyStdOverride_Serie5000_KeepsSerie()
    {
        // 5000 → Serie, même spécificité (6) → pas de surcharge
        var result = UnifiedCategoryResolver.ApplyStdOverride(UnifiedCategory.Serie, 5000);
        Assert.Equal(UnifiedCategory.Serie, result);
    }

    [Fact]
    public void ApplyStdOverride_Film2000_DoesNotBeatSerie()
    {
        // Film spécificité=5 < Serie=6 → pas de surcharge
        var result = UnifiedCategoryResolver.ApplyStdOverride(UnifiedCategory.Serie, 2000);
        Assert.Equal(UnifiedCategory.Serie, result);
    }

    [Fact]
    public void ApplyStdOverride_NullStdId_KeepsMap()
    {
        // stdId null → fromMap inchangé
        var result = UnifiedCategoryResolver.ApplyStdOverride(UnifiedCategory.Film, null);
        Assert.Equal(UnifiedCategory.Film, result);
    }

    // ─── Comics (R4) ──────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_ComicsChildStdId_ReturnsComic()
    {
        // [7000,7035] → NormalizeCategoryIds supprime parent 7000 → stdId=7035
        // Fix R4 : stdId seul dans [7030,7039] suffit (pas besoin de specId)
        var resolver = new UnifiedCategoryResolver();
        var result = resolver.Resolve("any", null, null, new[] { 7000, 7035 });
        Assert.Equal(UnifiedCategory.Comic, result);
    }

    [Fact]
    public void Resolve_BookParentOnly_ReturnsBook()
    {
        // 7000 seul (pas d'enfant comics) → Book
        var resolver = new UnifiedCategoryResolver();
        var result = resolver.Resolve("any", null, null, new[] { 7000 });
        Assert.Equal(UnifiedCategory.Book, result);
    }

    [Fact]
    public void ResolveStdSpec_ComicsNormalized_ChildStd7035()
    {
        // Après NormalizeCategoryIds [7000,7035] → parent supprimé → stdId=7035
        var (stdId, _) = UnifiedCategoryResolver.ResolveStdSpec(null, null, new[] { 7035 });
        Assert.Equal(7035, stdId);
    }

    [Fact]
    public void Resolve_ComicsLegacy_StdParentAndSpecChild_ReturnsComic()
    {
        // Ancienne forme : stdId=7000 (parent) + specId=7035 → Comic (règle compat)
        var resolver = new UnifiedCategoryResolver();
        var result = resolver.Resolve("any", 7000, 7035, new[] { 7000 });
        Assert.Equal(UnifiedCategory.Comic, result);
    }
}
