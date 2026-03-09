using System.Net;
using System.Text;
using Dapper;
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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace Feedarr.Api.Tests;

public sealed class PosterFetchServiceProviderLimiterTests
{
    [Fact]
    public async Task SaveTmdbPosterAsync_RespectsConfiguredTmdbConcurrencyLimit()
    {
        var handler = new SlowTmdbHandler();
        using var ctx = new PosterFetchLimiterContext(handler);
        var releaseIds = Enumerable.Range(0, 4)
            .Select(i => ctx.CreateRelease($"Matrix {i}", 1999 + i))
            .ToArray();

        var tasks = releaseIds.Select((releaseId, index) =>
            ctx.Posters.SaveTmdbPosterAsync(releaseId, 100 + index, "/poster.jpg", CancellationToken.None, logSingle: false));

        await Task.WhenAll(tasks);

        Assert.True(handler.MaxInFlight <= 2, $"Observed max TMDB concurrency {handler.MaxInFlight}");
    }

    [Fact]
    public async Task SaveTmdbPosterAsync_WithTvMediaType_DoesNotCrashWhenMovieEndpointReturns404()
    {
        using var ctx = new PosterFetchLimiterContext(new TvOnlyTmdbHandler());
        var releaseId = ctx.CreateRelease("Oedo Fire Slayer", 2026, mediaType: "tv");

        var posterUrl = await ctx.Posters.SaveTmdbPosterAsync(releaseId, 4242, "/poster.jpg", CancellationToken.None, logSingle: false);

        Assert.False(string.IsNullOrWhiteSpace(posterUrl));
        var release = ctx.Releases.GetForPoster(releaseId);
        Assert.NotNull(release);
        Assert.Equal("tmdb-4242-manual.jpg", release!.PosterFile);
    }

    private sealed class PosterFetchLimiterContext : IDisposable
    {
        private readonly TestWorkspace _workspace;
        private readonly TestWebHostEnvironment _env;

        public PosterFetchLimiterContext(HttpMessageHandler tmdbHandler)
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
            Settings = new SettingsRepository(Db, protection, NullLogger<SettingsRepository>.Instance);
            Settings.SaveMaintenance(new Models.Settings.MaintenanceSettings
            {
                ProviderRateLimitMode = "manual",
                ProviderConcurrencyManual = new Models.Settings.ProviderConcurrencyManualSettings
                {
                    Tmdb = 2,
                    Igdb = 1,
                    Fanart = 1,
                    Tvmaze = 1,
                    Others = 1
                }
            });

            var stats = new ProviderStatsService(new StatsRepository(Db, new MemoryCache(new MemoryCacheOptions())));
            var registry = new ExternalProviderRegistry();
            var instances = new ExternalProviderInstanceRepository(
                Db,
                Settings,
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

            var limiter = new ExternalProviderLimiter(Settings, NullLogger<ExternalProviderLimiter>.Instance);
            Releases = new ReleaseRepository(Db, new TitleParser(), new UnifiedCategoryResolver());
            Posters = new PosterFetchService(
                Releases,
                new ActivityRepository(Db, new BadgeSignal()),
                new TmdbClient(new HttpClient(tmdbHandler), Settings, stats, activeResolver),
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
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
                NoOpPosterThumbQueue.Instance,
                limiter);

            using var conn = Db.Open();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            conn.Execute(
                """
                INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
                VALUES('test', 1, 'http://localhost:9117/api', 'key', 'query', @ts, @ts);
                """,
                new { ts });
            SourceId = conn.ExecuteScalar<long>("SELECT id FROM sources LIMIT 1;");
        }

        public Db Db { get; }
        public SettingsRepository Settings { get; }
        public ReleaseRepository Releases { get; }
        public PosterFetchService Posters { get; }
        public long SourceId { get; }

        public long CreateRelease(string title, int year, string mediaType = "movie")
        {
            using var conn = Db.Open();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return conn.ExecuteScalar<long>(
                """
                INSERT INTO releases(
                  source_id, guid, title, created_at_ts, published_at_ts,
                  title_clean, year, unified_category, media_type
                )
                VALUES(
                  @sourceId, @guid, @title, @ts, @ts,
                  @title, @year, 'Film', @mediaType
                );
                SELECT last_insert_rowid();
                """,
                new { sourceId = SourceId, guid = Guid.NewGuid().ToString("N"), title, ts, year, mediaType });
        }

        public void Dispose()
        {
            _workspace.Dispose();
        }
    }

    private sealed class SlowTmdbHandler : HttpMessageHandler
    {
        private int _inFlight;
        private int _maxInFlight;

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref _inFlight);
            UpdateMax(current);
            try
            {
                await Task.Delay(75, cancellationToken);
                var uri = request.RequestUri ?? new Uri("https://api.themoviedb.org/3/");
                var path = uri.AbsolutePath.ToLowerInvariant();

                if (uri.Host.Contains("image.tmdb.org", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(CreateValidImageBytes())
                    };
                }

                if (path.Contains("/movie/"))
                {
                    return JsonResponse(
                        "{\"title\":\"The Matrix\",\"overview\":\"Mock overview\",\"genres\":[],\"vote_average\":8,\"vote_count\":100}");
                }

                if (path.Contains("/tv/"))
                    return JsonResponse("{\"name\":\"The Matrix\",\"overview\":\"Mock overview\",\"genres\":[],\"vote_average\":8,\"vote_count\":100}");

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private void UpdateMax(int candidate)
        {
            while (true)
            {
                var snapshot = Volatile.Read(ref _maxInFlight);
                if (candidate <= snapshot)
                    return;

                if (Interlocked.CompareExchange(ref _maxInFlight, candidate, snapshot) == snapshot)
                    return;
            }
        }

        private static HttpResponseMessage JsonResponse(string json)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

        private static byte[] CreateValidImageBytes()
        {
            using var image = new Image<Rgba32>(600, 900, new Rgba32(220, 20, 60));
            using var ms = new MemoryStream();
            image.Save(ms, new JpegEncoder());
            return ms.ToArray();
        }
    }

    private sealed class TvOnlyTmdbHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? new Uri("https://api.themoviedb.org/3/");
            var path = uri.AbsolutePath.ToLowerInvariant();

            if (uri.Host.Contains("image.tmdb.org", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(CreateValidImageBytes())
                });
            }

            if (path.Contains("/movie/"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            if (path.Contains("/tv/") && path.Contains("/credits"))
            {
                return Task.FromResult(JsonResponse("{\"cast\":[],\"crew\":[]}"));
            }

            if (path.Contains("/tv/"))
            {
                return Task.FromResult(JsonResponse("{\"name\":\"Oedo Fire Slayer\",\"overview\":\"Mock overview\",\"genres\":[],\"vote_average\":8,\"vote_count\":100}"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string json)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

        private static byte[] CreateValidImageBytes()
        {
            using var image = new Image<Rgba32>(600, 900, new Rgba32(220, 20, 60));
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
            RootDir = Path.Combine(Path.GetTempPath(), "feedarr-provider-gating-tests", Guid.NewGuid().ToString("N"));
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
