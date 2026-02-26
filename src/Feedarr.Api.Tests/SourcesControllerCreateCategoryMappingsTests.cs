using System.Text.Json;
using Dapper;
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

public sealed class SourcesControllerCreateCategoryMappingsTests
{
    [Fact]
    public async Task CreateSource_WithCategories_PersistsActiveMappings()
    {
        using var ctx = TestContext.Create();

        var action = await ctx.Controller.Create(
            new SourceCreateDto
            {
                Name = "Mapped source",
                TorznabUrl = "http://localhost:9117/api",
                ApiKey = "key",
                AuthMode = "query",
                Categories = new List<SourceCategorySelectionDto>
                {
                    new()
                    {
                        Id = 2000,
                        Name = "Movies",
                        IsSub = false,
                        ParentId = null,
                        UnifiedKey = "film",
                        UnifiedLabel = "Films"
                    },
                    new()
                    {
                        Id = 5000,
                        Name = "TV",
                        IsSub = false,
                        ParentId = null,
                        UnifiedKey = "",
                        UnifiedLabel = ""
                    }
                }
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var sourceId = body.RootElement.GetProperty("id").GetInt64();

        var active = ctx.Sources.GetActiveCategoryIds(sourceId).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { 2000, 5000 }, active);

        using var conn = ctx.Db.Open();
        var mappings = conn.Query<MappingRow>(
            """
            SELECT
              cat_id as CatId,
              group_key as GroupKey,
              group_label as GroupLabel
            FROM source_category_mappings
            WHERE source_id = @sid
            ORDER BY cat_id;
            """,
            new { sid = sourceId }).ToList();

        Assert.Equal(2, mappings.Count);
        Assert.Equal("films", mappings.Single(m => m.CatId == 2000).GroupKey);
        Assert.Equal("Films", mappings.Single(m => m.CatId == 2000).GroupLabel);
        Assert.Null(mappings.Single(m => m.CatId == 5000).GroupKey);
    }

    private sealed class MappingRow
    {
        public int CatId { get; set; }
        public string? GroupKey { get; set; }
        public string? GroupLabel { get; set; }
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
            MemoryCache cache)
        {
            _workspace = workspace;
            Db = db;
            Sources = sources;
            Controller = controller;
            _cache = cache;
        }

        public Db Db { get; }
        public SourceRepository Sources { get; }
        public SourcesController Controller { get; }

        public static TestContext Create()
        {
            var workspace = new TestWorkspace();
            var db = CreateDb(workspace);
            new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

            var protection = new PassthroughProtectionService();
            var sources = new SourceRepository(db, protection);
            var providers = new ProviderRepository(db, protection);
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
                providers: providers,
                releases: null!,
                activity: activity,
                caps: caps,
                backupCoordinator: null!,
                syncOrchestration: null!,
                log: NullLogger<SourcesController>.Instance);

            return new TestContext(workspace, db, sources, controller, cache);
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

        public bool TryUnprotect(string protectedText, out string? plainText)
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
