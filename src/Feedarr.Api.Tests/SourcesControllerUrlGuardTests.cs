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

public sealed class SourcesControllerUrlGuardTests
{
    [Fact]
    public async Task Create_BlockedHost_ReturnsBadRequest_AndDoesNotPersistSource()
    {
        using var ctx = TestContext.Create();

        var result = await ctx.Controller.Create(
            new SourceCreateDto
            {
                Name = "Blocked source",
                TorznabUrl = "http://169.254.169.254/api",
                ApiKey = "key",
                AuthMode = "query"
            },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(ctx.Sources.List());
    }

    [Fact]
    public async Task Update_BlockedHost_ReturnsBadRequest_AndKeepsStoredUrl()
    {
        using var ctx = TestContext.Create();
        var sourceId = ctx.Sources.Create("Safe source", "http://localhost:9117/api", "key", "query");

        var result = await ctx.Controller.Update(
            sourceId,
            new SourceUpdateDto
            {
                TorznabUrl = "http://169.254.169.254/api"
            },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);

        var stored = ctx.Sources.Get(sourceId);
        Assert.NotNull(stored);
        Assert.Equal("http://localhost:9117/api", stored!.TorznabUrl);
    }

    [Fact]
    public async Task Test_BlockedHost_ReturnsBadRequest_BeforeNetworkCall()
    {
        using var ctx = TestContext.Create();

        var action = await ctx.Controller.Test(
            new SourceTestRequestDto
            {
                TorznabUrl = "http://169.254.169.254/api",
                ApiKey = "key",
                AuthMode = "query",
                RssLimit = 20
            },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
    }

    private sealed class TestContext : IDisposable
    {
        private readonly TestWorkspace _workspace;
        private readonly MemoryCache _cache;

        private TestContext(
            TestWorkspace workspace,
            SourceRepository sources,
            SourcesController controller,
            MemoryCache cache)
        {
            _workspace = workspace;
            Sources = sources;
            Controller = controller;
            _cache = cache;
        }

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

            return new TestContext(workspace, sources, controller, cache);
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
