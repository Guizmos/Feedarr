using System.Text;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Titles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace Feedarr.Api.Tests;

public sealed class PosterThumbWorkerTests
{
    [Fact]
    public async Task ProcessJobAsync_WhenThumbAlreadyExists_Skips()
    {
        using var ctx = new PosterThumbContext();
        ctx.CreateStoreOriginal("tmdb-550");
        ctx.CreateStoreThumb("tmdb-550", 500);

        var result = await ctx.Worker.ProcessJobAsync(
            new PosterThumbJob("tmdb-550", [500], PosterThumbJobReason.MissingThumb),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Skipped);
        Assert.Equal("already_exists", result.Reason);
    }

    [Fact]
    public async Task ProcessJobAsync_WhenThumbMissing_GeneratesFile()
    {
        using var ctx = new PosterThumbContext();
        ctx.CreateStoreOriginal("tmdb-550");

        var result = await ctx.Worker.ProcessJobAsync(
            new PosterThumbJob("tmdb-550", [500], PosterThumbJobReason.Warmup),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.Skipped);
        Assert.Equal("generated", result.Reason);
        Assert.True(File.Exists(Path.Combine(ctx.StoreDir, "tmdb-550", "w500.webp")));
    }

    private sealed class PosterThumbContext : IDisposable
    {
        private readonly TestWorkspace _workspace;
        private readonly TestWebHostEnvironment _env;

        public PosterThumbContext()
        {
            _workspace = new TestWorkspace();
            _env = new TestWebHostEnvironment(_workspace.RootDir);
            var options = OptionsFactory.Create(new AppOptions
            {
                DataDir = _workspace.DataDir,
                DbFileName = "feedarr.db"
            });

            Db = new Db(options);
            new MigrationsRunner(Db, NullLogger<MigrationsRunner>.Instance).Run();

            var protection = new PassthroughProtectionService();
            var settings = new SettingsRepository(Db, protection, NullLogger<SettingsRepository>.Instance);
            var registry = new ExternalProviderRegistry();
            var instances = new ExternalProviderInstanceRepository(
                Db,
                settings,
                protection,
                registry,
                NullLogger<ExternalProviderInstanceRepository>.Instance);
            var activeResolver = new ActiveExternalProviderConfigResolver(
                instances,
                registry,
                NullLogger<ActiveExternalProviderConfigResolver>.Instance);

            Posters = new PosterFetchService(
                new ReleaseRepository(Db, new TitleParser(), new UnifiedCategoryResolver()),
                new ActivityRepository(Db, new BadgeSignal()),
                null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
                new PosterMatchCacheService(Db),
                options,
                _env,
                new PosterMatchingOrchestrator(
                    new VideoMatchingStrategy(),
                    new GameMatchingStrategy(),
                    new AnimeMatchingStrategy(),
                    new AudioMatchingStrategy(),
                    new GenericMatchingStrategy()),
                activeResolver,
                NullLogger<PosterFetchService>.Instance,
                new PosterThumbService(NullLogger<PosterThumbService>.Instance),
                NoOpPosterThumbQueue.Instance);

            Worker = new PosterThumbWorker(
                NullLogger<PosterThumbWorker>.Instance,
                NoOpPosterThumbQueue.Instance,
                Posters);
        }

        public Db Db { get; }
        public PosterFetchService Posters { get; }
        public PosterThumbWorker Worker { get; }
        public string StoreDir => Posters.PosterStoreDirPath;

        public void CreateStoreOriginal(string storeDir)
        {
            var dir = Path.Combine(StoreDir, storeDir);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "original.jpg"), CreateValidImageBytes());
        }

        public void CreateStoreThumb(string storeDir, int width)
        {
            var dir = Path.Combine(StoreDir, storeDir);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"w{width}.webp"), Encoding.UTF8.GetBytes("existing-thumb"));
        }

        public void Dispose()
        {
            _workspace.Dispose();
        }

        private static byte[] CreateValidImageBytes()
        {
            using var image = new Image<Rgba32>(600, 900, new Rgba32(50, 120, 200));
            using var ms = new MemoryStream();
            image.Save(ms, new JpegEncoder());
            return ms.ToArray();
        }
    }

    private sealed class PassthroughProtectionService : IApiKeyProtectionService
    {
        public string Protect(string plainText) => plainText;
        public string Unprotect(string protectedText) => protectedText;
        public bool TryUnprotect(string protectedText, out string? plainText) { plainText = protectedText; return true; }
        public bool IsProtected(string value) => false;
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public TestWebHostEnvironment(string rootDir)
        {
            ApplicationName = "Feedarr.Api.Tests";
            EnvironmentName = "Test";
            ContentRootPath = rootDir;
            ContentRootFileProvider = new NullFileProvider();
            WebRootPath = rootDir;
            WebRootFileProvider = new NullFileProvider();
        }

        public string ApplicationName { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public string EnvironmentName { get; set; }
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            RootDir = Path.Combine(Path.GetTempPath(), "feedarr-thumb-worker-tests", Guid.NewGuid().ToString("N"));
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
