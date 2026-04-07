using System.Net;
using System.Text;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Dtos.Providers;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Tmdb;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class PostersControllerSearchLimiterTests
{
    [Fact]
    public async Task Search_DefaultMediaType_UsesLimiterForBothTmdbSearches()
    {
        using var ctx = new SearchLimiterContext();
        var limiter = new RecordingLimiter();
        var controller = ctx.CreateController(limiter);

        var result = await controller.Search("Matrix", null, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(2, limiter.Kinds.Count(kind => kind == ProviderKind.Tmdb));
    }

    [Fact]
    public async Task Search_SeriesMediaType_UsesLimiterWithExactlyOneTmdbCall()
    {
        using var ctx = new SearchLimiterContext();
        var limiter = new RecordingLimiter();
        var controller = ctx.CreateController(limiter);

        var result = await controller.Search("Matrix", "series", CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, limiter.Kinds.Count(kind => kind == ProviderKind.Tmdb));
        Assert.DoesNotContain(ProviderKind.Igdb, limiter.Kinds);
        Assert.DoesNotContain(ProviderKind.Others, limiter.Kinds);
    }

    [Fact]
    public async Task Search_MovieMediaType_UsesLimiterWithExactlyOneTmdbCall()
    {
        using var ctx = new SearchLimiterContext();
        var limiter = new RecordingLimiter();
        var controller = ctx.CreateController(limiter);

        var result = await controller.Search("Inception", "movie", CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, limiter.Kinds.Count(kind => kind == ProviderKind.Tmdb));
        Assert.DoesNotContain(ProviderKind.Igdb, limiter.Kinds);
        Assert.DoesNotContain(ProviderKind.Others, limiter.Kinds);
    }

    [Fact]
    public async Task Search_GameMediaType_UsesLimiterForIgdbAndOthers()
    {
        // IGDB and RAWG clients are null in the test controller — NRE is caught by SafeSearchAsync,
        // but the limiter still records the ProviderKind before the action is called.
        using var ctx = new SearchLimiterContext();
        var limiter = new RecordingLimiter();
        var controller = ctx.CreateController(limiter);

        var result = await controller.Search("Elden Ring", "game", CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, limiter.Kinds.Count(kind => kind == ProviderKind.Igdb));
        Assert.Equal(1, limiter.Kinds.Count(kind => kind == ProviderKind.Others));
        Assert.DoesNotContain(ProviderKind.Tmdb, limiter.Kinds);
    }

    private sealed class SearchLimiterContext : IDisposable
    {
        private readonly TestWorkspace _workspace;
        private readonly TestWebHostEnvironment _environment;

        public SearchLimiterContext()
        {
            _workspace = new TestWorkspace();
            _environment = new TestWebHostEnvironment(_workspace.RootDir);

            var options = OptionsFactory.Create(new AppOptions
            {
                DataDir = _workspace.DataDir,
                DbFileName = "feedarr.db"
            });

            var db = new Db(options);
            new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

            var protection = new PassthroughProtectionService();
            var settings = new SettingsRepository(db, protection, NullLogger<SettingsRepository>.Instance);
            var stats = new ProviderStatsService(new StatsRepository(db, new MemoryCache(new MemoryCacheOptions())));
            var registry = new ExternalProviderRegistry();
            var instances = new ExternalProviderInstanceRepository(
                db,
                settings,
                protection,
                registry,
                NullLogger<ExternalProviderInstanceRepository>.Instance);

            instances.Create(new ExternalProviderCreateDto
            {
                ProviderKey = ExternalProviderKeys.Tmdb,
                Enabled = true,
                Auth = new Dictionary<string, string?> { ["apiKey"] = "tmdb-key" }
            });

            var activeResolver = new ActiveExternalProviderConfigResolver(
                instances,
                registry,
                NullLogger<ActiveExternalProviderConfigResolver>.Instance);

            Tmdb = new TmdbClient(new HttpClient(new SearchTmdbHandler()), settings, stats, activeResolver);
        }

        public TmdbClient Tmdb { get; }

        public PostersController CreateController(IExternalProviderLimiter limiter)
        {
            var controller = new PostersController(
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                Tmdb,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                NullLogger<PostersController>.Instance,
                null,
                null,
                limiter);

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
            return controller;
        }

        public void Dispose()
        {
            _workspace.Dispose();
        }
    }

    private sealed class RecordingLimiter : IExternalProviderLimiter
    {
        public List<ProviderKind> Kinds { get; } = [];

        public async Task<T> RunAsync<T>(ProviderKind kind, Func<CancellationToken, Task<T>> action, CancellationToken ct)
        {
            Kinds.Add(kind);
            return await action(ct);
        }

        public async Task RunAsync(ProviderKind kind, Func<CancellationToken, Task> action, CancellationToken ct)
        {
            Kinds.Add(kind);
            await action(ct);
        }
    }

    private sealed class SearchTmdbHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath?.ToLowerInvariant() ?? string.Empty;
            if (path.Contains("/search/movie"))
            {
                return Task.FromResult(JsonResponse(
                    "{\"results\":[{\"id\":100,\"title\":\"The Matrix\",\"original_title\":\"The Matrix\",\"poster_path\":\"/movie.jpg\",\"release_date\":\"1999-03-31\",\"original_language\":\"en\"}]}"));
            }

            if (path.Contains("/search/tv"))
            {
                return Task.FromResult(JsonResponse(
                    "{\"results\":[{\"id\":200,\"name\":\"The Matrix Show\",\"original_name\":\"The Matrix Show\",\"poster_path\":\"/tv.jpg\",\"first_air_date\":\"1999-03-31\",\"original_language\":\"en\"}]}"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string json)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
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
            RootDir = Path.Combine(Path.GetTempPath(), "feedarr-search-limiter-tests", Guid.NewGuid().ToString("N"));
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
