using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Dtos.Sources;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class SourcesControllerCategoryMappingsPatchTests
{
    [Fact]
    public void PatchMappings_AliasFilm_IsStoredAsCanonicalFilmsLabel()
    {
        using var ctx = TestContext.Create();

        var result = ctx.Controller.PatchCategoryMappings(
            ctx.SourceId,
            new SourceCategoryMappingsPatchDto
            {
                SelectedCategoryIds = new List<int> { 2020 },
                Mappings =
                {
                    new SourceCategoryMappingPatchItemDto
                    {
                        CatId = 2020,
                        GroupKey = "film",
                        GroupLabel = "Label bidon"
                    }
                }
            });

        Assert.IsType<OkObjectResult>(result);
        var mapping = Assert.Single(ctx.Sources.GetCategoryMappings(ctx.SourceId));
        Assert.Equal(2020, mapping.CatId);
        Assert.Equal("films", mapping.GroupKey);
        Assert.Equal("Films", mapping.GroupLabel);
    }

    [Fact]
    public void PatchMappings_AliasShows_IsStoredAsCanonicalEmissions()
    {
        using var ctx = TestContext.Create();

        var result = ctx.Controller.PatchCategoryMappings(
            ctx.SourceId,
            new SourceCategoryMappingsPatchDto
            {
                SelectedCategoryIds = new List<int> { 5050 },
                Mappings =
                {
                    new SourceCategoryMappingPatchItemDto { CatId = 5050, GroupKey = "shows" }
                }
            });

        Assert.IsType<OkObjectResult>(result);
        var mapping = Assert.Single(ctx.Sources.GetCategoryMappings(ctx.SourceId));
        Assert.Equal("emissions", mapping.GroupKey);
        Assert.Equal("Emissions", mapping.GroupLabel);
    }

    [Fact]
    public void PatchMappings_InvalidOther_ReturnsBadRequest()
    {
        using var ctx = TestContext.Create();

        var result = ctx.Controller.PatchCategoryMappings(
            ctx.SourceId,
            new SourceCategoryMappingsPatchDto
            {
                SelectedCategoryIds = new List<int>(),
                Mappings =
                {
                    new SourceCategoryMappingPatchItemDto { CatId = 2020, GroupKey = "other" }
                }
            });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(ctx.Sources.GetCategoryMappings(ctx.SourceId));
    }

    [Fact]
    public void PatchMappings_MissingSelectedCategoryIds_ReturnsBadRequest()
    {
        using var ctx = TestContext.Create();

        var result = Assert.IsType<BadRequestObjectResult>(ctx.Controller.PatchCategoryMappings(
            ctx.SourceId,
            new SourceCategoryMappingsPatchDto
            {
                SelectedCategoryIds = null,
                Mappings =
                {
                    new SourceCategoryMappingPatchItemDto { CatId = 2020, GroupKey = "films" }
                }
            }));
        var error = result.Value?.GetType().GetProperty("error")?.GetValue(result.Value)?.ToString();
        Assert.Equal("selectedCategoryIds missing; send selectedCategoryIds: [] if empty", error);
    }

    [Fact]
    public void PatchMappings_LegacyCategoryIds_IsAccepted()
    {
        using var ctx = TestContext.Create();

        var result = ctx.Controller.PatchCategoryMappings(
            ctx.SourceId,
            new SourceCategoryMappingsPatchDto
            {
                SelectedCategoryIds = null,
                CategoryIds = new List<int> { 2020, 5050, 2020 },
                Mappings =
                {
                    new SourceCategoryMappingPatchItemDto { CatId = 2020, GroupKey = "films" },
                    new SourceCategoryMappingPatchItemDto { CatId = 5050, GroupKey = "series" }
                }
            });

        Assert.IsType<OkObjectResult>(result);
        var selected = ctx.Sources.GetSelectedCategoryIds(ctx.SourceId).OrderBy(id => id).ToArray();
        Assert.Equal(new[] { 2020, 5050 }, selected);
    }

    [Fact]
    public void PatchMappings_GroupKeyNull_RemovesMapping()
    {
        using var ctx = TestContext.Create();

        var upsertResult = ctx.Controller.PatchCategoryMappings(
            ctx.SourceId,
            new SourceCategoryMappingsPatchDto
            {
                SelectedCategoryIds = new List<int> { 2020 },
                Mappings =
                {
                    new SourceCategoryMappingPatchItemDto { CatId = 2020, GroupKey = "films" }
                }
            });
        Assert.IsType<OkObjectResult>(upsertResult);
        Assert.Single(ctx.Sources.GetCategoryMappings(ctx.SourceId));

        var deleteResult = ctx.Controller.PatchCategoryMappings(
            ctx.SourceId,
            new SourceCategoryMappingsPatchDto
            {
                SelectedCategoryIds = new List<int>(),
                Mappings =
                {
                    new SourceCategoryMappingPatchItemDto
                    {
                        CatId = 2020,
                        GroupKey = null,
                        GroupLabel = "Ignored"
                    }
                }
            });

        Assert.IsType<OkObjectResult>(deleteResult);
        Assert.Empty(ctx.Sources.GetCategoryMappings(ctx.SourceId));
    }

    [Fact]
    public void PatchMappings_MixedPayload_IsCanonicalized()
    {
        using var ctx = TestContext.Create();

        var result = ctx.Controller.PatchCategoryMappings(
            ctx.SourceId,
            new SourceCategoryMappingsPatchDto
            {
                SelectedCategoryIds = new List<int> { 2020, 5050, 7000 },
                Mappings =
                {
                    new SourceCategoryMappingPatchItemDto { CatId = 2020, GroupKey = "film" },
                    new SourceCategoryMappingPatchItemDto { CatId = 5050, GroupKey = "shows" },
                    new SourceCategoryMappingPatchItemDto { CatId = 7000, GroupKey = "books" }
                }
            });

        Assert.IsType<OkObjectResult>(result);

        var byCat = ctx.Sources.GetCategoryMappings(ctx.SourceId).ToDictionary(m => m.CatId);
        Assert.Equal("films", byCat[2020].GroupKey);
        Assert.Equal("emissions", byCat[5050].GroupKey);
        Assert.Equal("books", byCat[7000].GroupKey);
        Assert.Equal("Livres", byCat[7000].GroupLabel);

        var selected = ctx.Sources.GetSelectedCategoryIds(ctx.SourceId).OrderBy(id => id).ToArray();
        Assert.Equal(new[] { 2020, 5050, 7000 }, selected);
    }

    private sealed class TestContext : IDisposable
    {
        private readonly TestWorkspace _workspace;
        private readonly MemoryCache _cache;

        private TestContext(
            TestWorkspace workspace,
            Db db,
            SourceRepository sources,
            SourcesController controller,
            long sourceId,
            MemoryCache cache)
        {
            _workspace = workspace;
            Db = db;
            Sources = sources;
            Controller = controller;
            SourceId = sourceId;
            _cache = cache;
        }

        public Db Db { get; }
        public SourceRepository Sources { get; }
        public SourcesController Controller { get; }
        public long SourceId { get; }

        public static TestContext Create()
        {
            var workspace = new TestWorkspace();
            var db = CreateDb(workspace);
            new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

            var sources = new SourceRepository(db, new PassthroughProtectionService());
            var sourceId = sources.Create("Test source", "http://localhost:9117/api", "key", "query");

            var activity = new ActivityRepository(db, new BadgeSignal());
            var cache = new MemoryCache(new MemoryCacheOptions());
            var caps = new CategoryRecommendationService(
                sources,
                torznab: null!,
                cache,
                NullLogger<CategoryRecommendationService>.Instance);

            var controller = new SourcesController(
                torznab: null!,
                sources: sources,
                providers: null!,
                releases: null!,
                activity: activity,
                caps: caps,
                backupCoordinator: null!,
                syncOrchestration: null!,
                log: NullLogger<SourcesController>.Instance);

            return new TestContext(workspace, db, sources, controller, sourceId, cache);
        }

        public void Dispose()
        {
            _cache.Dispose();
            _workspace.Dispose();
        }
    }

    private sealed class PassthroughProtectionService : IApiKeyProtectionService
    {
        public string Protect(string plainText) => plainText;
        public string Unprotect(string protectedText) => protectedText;

        public bool TryUnprotect(string protectedText, out string plainText)
        {
            plainText = protectedText;
            return true;
        }

        public bool IsProtected(string value) => false;
    }

    private static Db CreateDb(TestWorkspace workspace)
    {
        var options = OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        });
        return new Db(options);
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            RootDir = Path.Combine(Path.GetTempPath(), "feedarr-tests", Guid.NewGuid().ToString("N"));
            DataDir = Path.Combine(RootDir, "data");
            Directory.CreateDirectory(DataDir);
        }

        public string RootDir { get; }
        public string DataDir { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootDir))
                    Directory.Delete(RootDir, true);
            }
            catch
            {
            }
        }
    }
}
