using System.Net;
using System.Text;
using Dapper;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Dtos.Providers;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.ComicVine;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Fanart;
using Feedarr.Api.Services.GoogleBooks;
using Feedarr.Api.Services.Igdb;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.MusicBrainz;
using Feedarr.Api.Services.OpenLibrary;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Rawg;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.TheAudioDb;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Tmdb;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class PostersControllerSecurityAndSearchTests
{
    [Theory]
    [InlineData("../a.jpg")]
    [InlineData("..\\a.jpg")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    public void GetPoster_RejectsUnsafeStoredPosterPath(string posterFile)
    {
        using var context = new PosterControllerContext();
        var releaseId = context.CreateRelease(posterFile: posterFile);
        var controller = context.CreateController();

        var result = controller.GetPoster(releaseId);

        Assert.IsType<NotFoundResult>(result);
    }

    [Theory]
    [InlineData("../a.jpg")]
    [InlineData("..\\a.jpg")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    public void GetEntityPoster_RejectsUnsafeStoredPosterPath(string posterFile)
    {
        using var context = new PosterControllerContext();
        var entityId = context.CreateEntity(posterFile);
        var controller = context.CreateController();

        var result = controller.GetEntityPoster(entityId);

        Assert.IsType<NotFoundResult>(result);
    }

    [Theory]
    [InlineData("../a.jpg")]
    [InlineData("..\\a.jpg")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    public async Task GetBanner_RejectsUnsafeFallbackPosterPath(string posterFile)
    {
        using var context = new PosterControllerContext();
        var releaseId = context.CreateRelease(posterFile: posterFile, tmdbId: 0, tvdbId: 0);
        var controller = context.CreateController();

        var result = await controller.GetBanner(releaseId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void GetPoster_ReturnsPhysicalFileForSafeStoredPosterPath()
    {
        using var context = new PosterControllerContext();
        var releaseId = context.CreateRelease(posterFile: "safe.jpg");
        context.CreatePosterFile("safe.jpg");
        var controller = context.CreateController();

        var result = controller.GetPoster(releaseId);

        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(Path.Combine(context.PostersDir, "safe.jpg"), fileResult.FileName);
        Assert.Equal("image/jpeg", fileResult.ContentType);
    }

    [Fact]
    public async Task Search_WithoutMediaType_StartsMovieAndTvQueriesInParallel()
    {
        var handler = new ParallelTmdbHandler();
        using var context = new PosterControllerContext(handler);
        var controller = context.CreateController(context.TmdbClient);

        var searchTask = controller.Search("Matrix", null, CancellationToken.None);

        try
        {
            await handler.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            handler.ReleaseResponses.TrySetResult(true);
        }

        var result = await searchTask;
        Assert.IsType<OkObjectResult>(result);
    }

    private sealed class PosterControllerContext : IDisposable
    {
        private readonly TestWorkspace _workspace;
        private readonly TestWebHostEnvironment _environment;
        private readonly SettingsRepository _settings;
        private readonly PassthroughProtectionService _protection;
        private readonly ExternalProviderRegistry _registry;
        private readonly ExternalProviderInstanceRepository _externalInstances;

        public PosterControllerContext(HttpMessageHandler? tmdbHandler = null)
        {
            _workspace = new TestWorkspace();
            _environment = new TestWebHostEnvironment(_workspace.RootDir);
            Options = OptionsFactory.Create(new AppOptions
            {
                DataDir = _workspace.DataDir,
                DbFileName = "feedarr.db"
            });

            Db = new Db(Options);
            new MigrationsRunner(Db, NullLogger<MigrationsRunner>.Instance).Run();

            _protection = new PassthroughProtectionService();
            _settings = new SettingsRepository(Db, _protection, NullLogger<SettingsRepository>.Instance);
            Releases = new ReleaseRepository(Db, new TitleParser(), new UnifiedCategoryResolver());
            MediaEntities = new MediaEntityRepository(Db);
            Activity = new ActivityRepository(Db, new BadgeSignal());
            Stats = new ProviderStatsService(new StatsRepository(Db, new MemoryCache(new MemoryCacheOptions())));
            _registry = new ExternalProviderRegistry();
            _externalInstances = new ExternalProviderInstanceRepository(
                Db,
                _settings,
                _protection,
                _registry,
                NullLogger<ExternalProviderInstanceRepository>.Instance);

            ActiveResolver = new ActiveExternalProviderConfigResolver(
                _externalInstances,
                _registry,
                NullLogger<ActiveExternalProviderConfigResolver>.Instance);

            if (tmdbHandler is not null)
            {
                _settings.SaveExternalPartial(new ExternalSettings
                {
                    TmdbApiKey = "tmdb-key",
                    TmdbEnabled = true
                });
                _externalInstances.UpsertFromLegacyDefaultsAsync().GetAwaiter().GetResult();
                TmdbClient = new TmdbClient(new HttpClient(tmdbHandler), _settings, Stats, ActiveResolver);
            }

            PosterFetch = new PosterFetchService(
                Releases,
                Activity,
                TmdbClient ?? null!,
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
                Options,
                _environment,
                new PosterMatchingOrchestrator(
                    new VideoMatchingStrategy(),
                    new GameMatchingStrategy(),
                    new AnimeMatchingStrategy(),
                    new AudioMatchingStrategy(),
                    new GenericMatchingStrategy()),
                ActiveResolver,
                NullLogger<PosterFetchService>.Instance);

            RetroLogs = new RetroFetchLogService(Options, _environment);

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
        public MediaEntityRepository MediaEntities { get; }
        public ActivityRepository Activity { get; }
        public PosterFetchService PosterFetch { get; }
        public RetroFetchLogService RetroLogs { get; }
        public ProviderStatsService Stats { get; }
        public ActiveExternalProviderConfigResolver ActiveResolver { get; }
        public TmdbClient? TmdbClient { get; }
        public Microsoft.Extensions.Options.IOptions<AppOptions> Options { get; }
        public long SourceId { get; }
        public string PostersDir => PosterFetch.PostersDirPath;

        public PostersController CreateController(TmdbClient? tmdb = null)
        {
            return new PostersController(
                Releases,
                MediaEntities,
                Activity,
                PosterFetch,
                new NoOpPosterFetchQueue(),
                new PosterFetchJobFactory(Releases),
                RetroLogs,
                tmdb ?? null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                NullLogger<PostersController>.Instance);
        }

        public long CreateRelease(string? posterFile = null, int tmdbId = 0, int tvdbId = 0)
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
                  poster_file,
                  tmdb_id,
                  tvdb_id
                )
                VALUES(
                  @sourceId,
                  @guid,
                  'The Matrix',
                  @ts,
                  @ts,
                  'The Matrix',
                  1999,
                  'Film',
                  'movie',
                  @posterFile,
                  @tmdbId,
                  @tvdbId
                );
                SELECT last_insert_rowid();
                """,
                new
                {
                    sourceId = SourceId,
                    guid = Guid.NewGuid().ToString("N"),
                    ts,
                    posterFile,
                    tmdbId = tmdbId > 0 ? (int?)tmdbId : null,
                    tvdbId = tvdbId > 0 ? (int?)tvdbId : null
                });
        }

        public long CreateEntity(string? posterFile)
        {
            using var conn = Db.Open();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return conn.ExecuteScalar<long>(
                """
                INSERT INTO media_entities(
                  unified_category,
                  title_clean,
                  year,
                  poster_file,
                  poster_updated_at_ts,
                  created_at_ts,
                  updated_at_ts
                )
                VALUES(
                  'Film',
                  'The Matrix',
                  1999,
                  @posterFile,
                  @ts,
                  @ts,
                  @ts
                );
                SELECT last_insert_rowid();
                """,
                new { posterFile, ts });
        }

        public void CreatePosterFile(string fileName)
        {
            Directory.CreateDirectory(PostersDir);
            File.WriteAllBytes(Path.Combine(PostersDir, fileName), Encoding.UTF8.GetBytes("poster"));
        }

        public void Dispose()
        {
            _workspace.Dispose();
        }
    }

    private sealed class ParallelTmdbHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> BothStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseResponses { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _movieStarted;
        private int _tvStarted;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/search/movie", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Exchange(ref _movieStarted, 1);
                SignalBothStartedIfReady();
                await ReleaseResponses.Task.WaitAsync(cancellationToken);
                return JsonResponse("""
                    {"results":[{"id":100,"title":"The Matrix","original_title":"The Matrix","poster_path":"/movie.jpg","release_date":"1999-03-31","original_language":"en"}]}
                    """);
            }

            if (path.Contains("/search/tv", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Exchange(ref _tvStarted, 1);
                SignalBothStartedIfReady();
                await ReleaseResponses.Task.WaitAsync(cancellationToken);
                return JsonResponse("""
                    {"results":[{"id":200,"name":"The Matrix Show","original_name":"The Matrix Show","poster_path":"/tv.jpg","first_air_date":"1999-03-31","original_language":"en"}]}
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private void SignalBothStartedIfReady()
        {
            if (Volatile.Read(ref _movieStarted) == 1 && Volatile.Read(ref _tvStarted) == 1)
                BothStarted.TrySetResult(true);
        }

        private static HttpResponseMessage JsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class NoOpPosterFetchQueue : IPosterFetchQueue
    {
        public bool TryEnqueue(PosterFetchJob job) => true;
        public ValueTask<PosterFetchJob> DequeueAsync(CancellationToken ct) => ValueTask.FromCanceled<PosterFetchJob>(ct);
        public int ClearPending() => 0;
        public int Count => 0;
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
