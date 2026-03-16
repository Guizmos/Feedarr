using System.Net;
using System.Text;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Dtos.Providers;
using Feedarr.Api.Models;
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
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class PosterFetchStoreMaterializationTests
{
    [Fact]
    public async Task FetchPosterAsync_AutoDownload_MaterializesStore()
    {
        using var ctx = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var releaseId = ctx.CreateRelease("The Matrix", "The Matrix", 1999);

        var result = await ctx.Posters.FetchPosterAsync(releaseId, CancellationToken.None, logSingle: false, skipIfExists: true);

        Assert.True(result.Ok);

        var release = ctx.Releases.GetForPoster(releaseId);
        Assert.NotNull(release);
        Assert.Equal("tmdb:100", release!.PosterKey);
        Assert.Equal("tmdb-100", release.PosterStoreDir);
        Assert.True(File.Exists(Path.Combine(ctx.StoreDir, "tmdb-100", "w500.webp")));

        using var conn = ctx.Db.Open();
        var refCount = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM poster_store_refs WHERE store_dir = @storeDir AND release_id = @releaseId;",
            new { storeDir = "tmdb-100", releaseId });
        Assert.Equal(1, refCount);
    }

    [Fact]
    public async Task FetchPosterAsync_SkipIfExists_MaterializesStoreFromLegacyFile()
    {
        using var ctx = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var releaseId = ctx.CreateRelease(
            "Cached Movie",
            "Cached Movie",
            1999,
            posterFile: "tmdb-550-w500.jpg",
            posterProvider: "tmdb",
            posterProviderId: "550");

        ctx.WriteLegacyPoster("tmdb-550-w500.jpg", PosterStoreContext.CreateValidImageBytes());

        var result = await ctx.Posters.FetchPosterAsync(releaseId, CancellationToken.None, logSingle: false, skipIfExists: true);

        Assert.True(result.Ok);

        var release = ctx.Releases.GetForPoster(releaseId);
        Assert.NotNull(release);
        Assert.Equal("tmdb:550", release!.PosterKey);
        Assert.Equal("tmdb-550", release.PosterStoreDir);
        Assert.True(File.Exists(Path.Combine(ctx.StoreDir, "tmdb-550", "original.jpg")));
        Assert.True(File.Exists(Path.Combine(ctx.StoreDir, "tmdb-550", "w500.webp")));

        using var conn = ctx.Db.Open();
        var refCount = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM poster_store_refs WHERE store_dir = @storeDir AND release_id = @releaseId;",
            new { storeDir = "tmdb-550", releaseId });
        Assert.Equal(1, refCount);
    }

    [Fact]
    public async Task FetchPosterAsync_InvalidImageBytes_StaysBestEffort()
    {
        using var ctx = new PosterStoreContext(ImagePayloadMode.InvalidImage);
        var releaseId = ctx.CreateRelease("Broken Poster", "Broken Poster", 1999);

        var result = await ctx.Posters.FetchPosterAsync(releaseId, CancellationToken.None, logSingle: false, skipIfExists: true);

        Assert.True(result.Ok);

        var release = ctx.Releases.GetForPoster(releaseId);
        Assert.NotNull(release);
        Assert.Equal("tmdb-100-w500.jpg", release!.PosterFile);
        Assert.True(File.Exists(Path.Combine(ctx.PostersDir, "tmdb-100-w500.jpg")));
        Assert.False(File.Exists(Path.Combine(ctx.StoreDir, "tmdb-100", "w500.webp")));
        Assert.Contains(ctx.ThumbLogger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task FetchPosterAsync_SkipIfExists_WithTmdbAndMissingMetadata_RefreshesMetadata()
    {
        using var ctx = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var releaseId = ctx.CreateRelease(
            "Metadata Missing",
            "Metadata Missing",
            1999,
            posterFile: "tmdb-100-w500.jpg",
            posterProvider: "tmdb",
            posterProviderId: "100",
            tmdbId: 100);

        ctx.WriteLegacyPoster("tmdb-100-w500.jpg", PosterStoreContext.CreateValidImageBytes());

        var result = await ctx.Posters.FetchPosterAsync(releaseId, CancellationToken.None, logSingle: false, skipIfExists: true);

        Assert.True(result.Ok);
        var release = ctx.Releases.GetForPoster(releaseId);
        Assert.NotNull(release);
        Assert.True((release!.ExtUpdatedAtTs ?? 0) > 0);
        Assert.Equal("tmdb", release.ExtProvider);
        Assert.Equal(1, ctx.TmdbHandler.MovieDetailsCalls);
    }

    [Fact]
    public async Task FetchPosterAsync_MatchCacheReuse_WithTmdbAndMissingMetadata_RefreshesMetadata()
    {
        using var ctx = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var releaseId = ctx.CreateRelease("Cache Movie", "Cache Movie", 2022);

        ctx.WriteLegacyPoster("tmdb-100-w500.jpg", PosterStoreContext.CreateValidImageBytes());
        ctx.SeedPosterMatch(
            mediaType: "movie",
            titleClean: "Cache Movie",
            year: 2022,
            posterFile: "tmdb-100-w500.jpg",
            tmdbId: 100,
            matchSource: "tmdb",
            posterProvider: "tmdb",
            posterProviderId: "100");

        var result = await ctx.Posters.FetchPosterAsync(releaseId, CancellationToken.None, logSingle: false, skipIfExists: true);

        Assert.True(result.Ok);
        var release = ctx.Releases.GetForPoster(releaseId);
        Assert.NotNull(release);
        Assert.Equal("tmdb-100-w500.jpg", release!.PosterFile);
        Assert.True((release.ExtUpdatedAtTs ?? 0) > 0);
        Assert.Equal("tmdb", release.ExtProvider);
        Assert.Equal(1, ctx.TmdbHandler.MovieDetailsCalls);
    }

    [Fact]
    public async Task FetchPosterAsync_SkipIfExists_WithExistingMetadata_DoesNotRefreshMetadata()
    {
        using var ctx = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var existingTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 120;
        var releaseId = ctx.CreateRelease(
            "Metadata Present",
            "Metadata Present",
            1999,
            posterFile: "tmdb-100-w500.jpg",
            posterProvider: "tmdb",
            posterProviderId: "100",
            tmdbId: 100,
            extProvider: "tmdb",
            extOverview: "already present",
            extUpdatedAtTs: existingTs);

        ctx.WriteLegacyPoster("tmdb-100-w500.jpg", PosterStoreContext.CreateValidImageBytes());

        var result = await ctx.Posters.FetchPosterAsync(releaseId, CancellationToken.None, logSingle: false, skipIfExists: true);

        Assert.True(result.Ok);
        var release = ctx.Releases.GetForPoster(releaseId);
        Assert.NotNull(release);
        Assert.Equal(existingTs, release!.ExtUpdatedAtTs);
        Assert.Equal("already present", release.ExtOverview);
        Assert.Equal(0, ctx.TmdbHandler.MovieDetailsCalls);
    }

    [Fact]
    public async Task FetchPosterAsync_SkipIfExists_WithoutTmdbId_DoesNotRefreshMetadata()
    {
        using var ctx = new PosterStoreContext(ImagePayloadMode.ValidImage);
        var releaseId = ctx.CreateRelease(
            "No Tmdb",
            "No Tmdb",
            1999,
            posterFile: "tmdb-100-w500.jpg",
            posterProvider: "tmdb",
            posterProviderId: "100",
            tmdbId: null);

        ctx.WriteLegacyPoster("tmdb-100-w500.jpg", PosterStoreContext.CreateValidImageBytes());

        var result = await ctx.Posters.FetchPosterAsync(releaseId, CancellationToken.None, logSingle: false, skipIfExists: true);

        Assert.True(result.Ok);
        var release = ctx.Releases.GetForPoster(releaseId);
        Assert.NotNull(release);
        Assert.Null(release!.ExtUpdatedAtTs);
        Assert.Equal(0, ctx.TmdbHandler.MovieDetailsCalls);
    }

    private enum ImagePayloadMode
    {
        ValidImage,
        InvalidImage
    }

    private sealed class PosterStoreContext : IDisposable
    {
        private readonly TestWorkspace _workspace;
        private readonly TestWebHostEnvironment _env;

        public PosterStoreContext(ImagePayloadMode imageMode)
        {
            _workspace = new TestWorkspace();
            _env = new TestWebHostEnvironment(_workspace.RootDir);

            var options = OptionsFactory.Create(new AppOptions
            {
                DataDir = _workspace.DataDir,
                DbFileName = "feedarr.db"
            });

            Db = new Db(options);
            new MigrationsRunner(Db, new ListLogger<MigrationsRunner>()).Run();

            var protection = new PassthroughProtectionService();
            var settings = new SettingsRepository(Db, protection, new ListLogger<SettingsRepository>());
            var stats = new ProviderStatsService(new StatsRepository(Db, new MemoryCache(new MemoryCacheOptions())));
            var registry = new ExternalProviderRegistry();
            var instances = new ExternalProviderInstanceRepository(
                Db,
                settings,
                protection,
                registry,
                new ListLogger<ExternalProviderInstanceRepository>());

            instances.Create(new ExternalProviderCreateDto
            {
                ProviderKey = ExternalProviderKeys.Tmdb,
                Enabled = true,
                Auth = new Dictionary<string, string?> { ["apiKey"] = "tmdb-key" }
            });

            var activeResolver = new ActiveExternalProviderConfigResolver(
                instances,
                registry,
                new ListLogger<ActiveExternalProviderConfigResolver>());

            Releases = new ReleaseRepository(Db, new TitleParser(), new UnifiedCategoryResolver());
            ThumbLogger = new ListLogger<PosterThumbService>();
            TmdbHandler = new TmdbStoreHandler(imageMode);

            Posters = new PosterFetchService(
                Releases,
                new ActivityRepository(Db, new BadgeSignal()),
                new TmdbClient(new HttpClient(TmdbHandler), settings, stats, activeResolver),
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
                new ListLogger<PosterFetchService>(),
                new PosterThumbService(ThumbLogger));

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
        public ReleaseRepository Releases { get; }
        public PosterFetchService Posters { get; }
        public ListLogger<PosterThumbService> ThumbLogger { get; }
        public TmdbStoreHandler TmdbHandler { get; }
        public long SourceId { get; }
        public string PostersDir => Posters.PostersDirPath;
        public string StoreDir => Posters.PosterStoreDirPath;

        public long CreateRelease(
            string title,
            string titleClean,
            int? year,
            string? posterFile = null,
            string? posterProvider = null,
            string? posterProviderId = null,
            int? tmdbId = null,
            string? extProvider = null,
            string? extOverview = null,
            long? extUpdatedAtTs = null)
        {
            using var conn = Db.Open();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return conn.ExecuteScalar<long>(
                """
                INSERT INTO releases(
                  source_id,
                  guid,
                  title,
                  created_at_ts,
                  published_at_ts,
                  title_clean,
                  year,
                  unified_category,
                  media_type,
                  tmdb_id,
                  poster_file,
                  poster_provider,
                  poster_provider_id,
                  ext_provider,
                  ext_overview,
                  ext_updated_at_ts
                )
                VALUES(
                  @sourceId,
                  @guid,
                  @title,
                  @ts,
                  @ts,
                  @titleClean,
                  @year,
                  'Film',
                  'movie',
                  @tmdbId,
                  @posterFile,
                  @posterProvider,
                  @posterProviderId,
                  @extProvider,
                  @extOverview,
                  @extUpdatedAtTs
                );
                SELECT last_insert_rowid();
                """,
                new
                {
                    sourceId = SourceId,
                    guid = Guid.NewGuid().ToString("N"),
                    title,
                    ts,
                    titleClean,
                    year,
                    tmdbId,
                    posterFile,
                    posterProvider,
                    posterProviderId,
                    extProvider,
                    extOverview,
                    extUpdatedAtTs
                });
        }

        public void SeedPosterMatch(
            string mediaType,
            string titleClean,
            int? year,
            string posterFile,
            int? tmdbId,
            string matchSource,
            string? posterProvider,
            string? posterProviderId)
        {
            var normalizedTitle = TitleNormalizer.NormalizeTitle(titleClean);
            var fingerprint = PosterMatchCacheService.BuildFingerprint(
                new PosterTitleKey(mediaType, normalizedTitle, year, null, null));
            var idsJson = PosterMatchCacheService.SerializeIds(new PosterMatchIds(tmdbId, null, null, null, null));
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var cache = new PosterMatchCacheService(Db);
            cache.Upsert(new Feedarr.Api.Services.Posters.PosterMatch(
                fingerprint: fingerprint,
                mediaType: mediaType,
                normalizedTitle: normalizedTitle,
                year: year,
                season: null,
                episode: null,
                idsJson: idsJson,
                confidence: 0.95,
                matchSource: matchSource,
                posterFile: posterFile,
                posterProvider: posterProvider,
                posterProviderId: posterProviderId,
                posterLang: null,
                posterSize: "w500",
                createdTs: now,
                lastSeenTs: now,
                lastAttemptTs: null,
                lastError: null));
        }

        public void WriteLegacyPoster(string fileName, byte[] bytes)
        {
            Directory.CreateDirectory(PostersDir);
            File.WriteAllBytes(Path.Combine(PostersDir, fileName), bytes);
        }

        public static byte[] CreateValidImageBytes()
        {
            using var image = new Image<Rgba32>(600, 900, new Rgba32(220, 20, 60));
            using var ms = new MemoryStream();
            image.Save(ms, new JpegEncoder());
            return ms.ToArray();
        }

        public void Dispose()
        {
            _workspace.Dispose();
        }
    }

    private sealed class TmdbStoreHandler : HttpMessageHandler
    {
        private readonly ImagePayloadMode _imageMode;
        public int MovieDetailsCalls { get; private set; }
        public int MovieCreditsCalls { get; private set; }

        public TmdbStoreHandler(ImagePayloadMode imageMode)
        {
            _imageMode = imageMode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? new Uri("https://api.themoviedb.org/3/");
            var path = uri.AbsolutePath.ToLowerInvariant();

            if (path.Contains("/search/movie"))
            {
                return Task.FromResult(JsonResponse(
                    "{\"results\":[{\"id\":100,\"title\":\"The Matrix\",\"original_title\":\"The Matrix\",\"poster_path\":\"/tmdb/matrix-search.jpg\",\"release_date\":\"1999-03-31\",\"original_language\":\"en\"}]}"));
            }

            if (path.Contains("/search/tv"))
                return Task.FromResult(JsonResponse("{\"results\":[]}"));

            if (path.Contains("/movie/100/images"))
            {
                return Task.FromResult(JsonResponse(
                    "{\"posters\":[{\"file_path\":\"/tmdb/matrix-pref.jpg\",\"iso_639_1\":\"en\",\"vote_average\":8,\"vote_count\":100,\"width\":1000,\"height\":1500}],\"backdrops\":[]}"));
            }

            if (path.Contains("/movie/100/credits"))
            {
                MovieCreditsCalls++;
                return Task.FromResult(JsonResponse("{\"cast\":[],\"crew\":[]}"));
            }

            if (path.Contains("/movie/100"))
            {
                MovieDetailsCalls++;
                return Task.FromResult(JsonResponse(
                    "{\"title\":\"The Matrix\",\"overview\":\"Mock overview\",\"genres\":[],\"vote_average\":8,\"vote_count\":100}"));
            }

            if (uri.Host.Contains("image.tmdb.org", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = _imageMode == ImagePayloadMode.ValidImage
                    ? PosterStoreContext.CreateValidImageBytes()
                    : Encoding.UTF8.GetBytes("not-an-image");

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string json)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
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
            RootDir = Path.Combine(Path.GetTempPath(), "feedarr-store-tests", Guid.NewGuid().ToString("N"));
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
