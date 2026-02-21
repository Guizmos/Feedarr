using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Dtos.Providers;
using Feedarr.Api.Models;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.ComicVine;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Fanart;
using Feedarr.Api.Services.GoogleBooks;
using Feedarr.Api.Services.Igdb;
using Feedarr.Api.Services.Jikan;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.TheAudioDb;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Tmdb;
using Feedarr.Api.Services.TvMaze;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

internal enum PosterContractScenario
{
    MovieTmdbHit,
    MovieTmdbFallbackFanart,
    SeriesTvmazeHit,
    SeriesTvmazeMissTmdb,
    SeriesTmdbFallbackFanart,
    EmissionAmbiguousTmdbFallback,
    CacheReuseMovie,
    GameIgdbHit,
    GameIgdbMiss,
    AnimeJikanHit,
    AudioAudioDbHit,
    BookGoogleBooksHit,
    ComicComicVineHit
}

internal sealed record PosterContractSnapshot(
    bool Ok,
    int StatusCode,
    string? PosterProvider,
    string? PosterFile,
    string? MatchSource,
    int? TmdbId,
    int? TvdbId,
    bool Cached,
    string? Error);

internal sealed class PosterMatchingContractTestRig : IDisposable
{
    private readonly TestWorkspace _workspace;
    private readonly Db _db;
    private readonly ReleaseRepository _releases;
    private readonly PosterFetchService _posters;

    public PosterMatchingContractTestRig(PosterContractScenario scenario)
    {
        _workspace = new TestWorkspace();

        _db = new Db(OptionsFactory.Create(new AppOptions
        {
            DataDir = _workspace.DataDir,
            DbFileName = "feedarr.db"
        }));

        new MigrationsRunner(_db, NullLogger<MigrationsRunner>.Instance).Run();

        var protection = new DeterministicProtectionService();
        var settings = new SettingsRepository(_db, protection, NullLogger<SettingsRepository>.Instance);
        var stats = new ProviderStatsService(new StatsRepository(_db));

        var registry = new ExternalProviderRegistry();
        var externalInstances = new ExternalProviderInstanceRepository(
            _db,
            settings,
            protection,
            registry,
            NullLogger<ExternalProviderInstanceRepository>.Instance);

        SeedExternalInstances(externalInstances);

        var resolver = new ActiveExternalProviderConfigResolver(
            externalInstances,
            registry,
            NullLogger<ActiveExternalProviderConfigResolver>.Instance);

        var tmdb = new TmdbClient(new HttpClient(new TmdbHandler(scenario)), settings, stats, resolver);
        var fanart = new FanartClient(new HttpClient(new FanartHandler(scenario)), stats, resolver);
        var igdb = new IgdbClient(new HttpClient(new IgdbHandler(scenario)), stats, resolver);
        var tvmaze = new TvMazeClient(new HttpClient(new TvMazeHandler(scenario)), stats, resolver);
        var jikan = new JikanClient(new HttpClient(new JikanHandler(scenario)), resolver, stats);
        var theAudioDb = new TheAudioDbClient(new HttpClient(new TheAudioDbHandler(scenario)), resolver, stats);
        var googleBooks = new GoogleBooksClient(new HttpClient(new GoogleBooksHandler(scenario)), resolver, stats);
        var comicVine = new ComicVineClient(new HttpClient(new ComicVineHandler(scenario)), resolver, stats);

        _releases = new ReleaseRepository(_db, new TitleParser(), new UnifiedCategoryResolver());
        var activity = new ActivityRepository(_db, new BadgeSignal());
        var matchCache = new PosterMatchCacheService(_db);
        var orchestrator = new PosterMatchingOrchestrator(
            new VideoMatchingStrategy(),
            new GameMatchingStrategy(),
            new AnimeMatchingStrategy(),
            new AudioMatchingStrategy(),
            new GenericMatchingStrategy());

        _posters = new PosterFetchService(
            _releases,
            activity,
            tmdb,
            fanart,
            igdb,
            tvmaze,
            jikan,
            theAudioDb,
            googleBooks,
            comicVine,
            matchCache,
            OptionsFactory.Create(new AppOptions
            {
                DataDir = _workspace.DataDir,
                DbFileName = "feedarr.db"
            }),
            new TestWebHostEnvironment(_workspace.RootDir),
            orchestrator,
            resolver);

        SeedSource();
    }

    public long CreateRelease(
        string title,
        string titleClean,
        int? year,
        UnifiedCategory unifiedCategory,
        string mediaType)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

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
              media_type
            )
            VALUES(
              1,
              @guid,
              @title,
              @ts,
              @ts,
              @titleClean,
              @year,
              @unifiedCategory,
              @mediaType
            );
            SELECT last_insert_rowid();
            """,
            new
            {
                guid = Guid.NewGuid().ToString("N"),
                title,
                ts = now,
                titleClean,
                year,
                unifiedCategory = unifiedCategory.ToString(),
                mediaType
            });
    }

    public async Task<PosterContractSnapshot> FetchSnapshotAsync(long releaseId)
    {
        var result = await _posters.FetchPosterAsync(releaseId, CancellationToken.None, logSingle: true, skipIfExists: true);
        var release = _releases.GetForPoster(releaseId);

        string? matchSource;
        using (var conn = _db.Open())
        {
            matchSource = conn.ExecuteScalar<string?>(
                "SELECT match_source FROM poster_matches ORDER BY last_seen_ts DESC, created_ts DESC LIMIT 1;");
        }

        var bodyJson = JsonSerializer.Serialize(result.Body);
        using var bodyDoc = JsonDocument.Parse(bodyJson);
        var root = bodyDoc.RootElement;

        return new PosterContractSnapshot(
            Ok: result.Ok,
            StatusCode: result.StatusCode,
            PosterProvider: release?.PosterProvider,
            PosterFile: release?.PosterFile,
            MatchSource: matchSource,
            TmdbId: release?.TmdbId,
            TvdbId: release?.TvdbId,
            Cached: TryGetBool(root, "cached"),
            Error: TryGetString(root, "error"));
    }

    private static bool TryGetBool(JsonElement root, string propertyName)
    {
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.True;
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (!root.TryGetProperty(propertyName, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private void SeedSource()
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        conn.Execute(
            """
            INSERT INTO sources(
              id,
              name,
              enabled,
              torznab_url,
              api_key,
              auth_mode,
              created_at_ts,
              updated_at_ts
            )
            VALUES(
              1,
              'Test Source',
              1,
              'http://localhost:9117/api',
              'x',
              'query',
              @ts,
              @ts
            );
            """,
            new { ts = now });
    }

    private static void SeedExternalInstances(ExternalProviderInstanceRepository repository)
    {
        repository.Create(new ExternalProviderCreateDto
        {
            ProviderKey = ExternalProviderKeys.Tmdb,
            Enabled = true,
            Auth = new Dictionary<string, string?> { ["apiKey"] = "tmdb-key" }
        });

        repository.Create(new ExternalProviderCreateDto
        {
            ProviderKey = ExternalProviderKeys.Tvmaze,
            Enabled = true,
            Auth = new Dictionary<string, string?> { ["apiKey"] = "" }
        });

        repository.Create(new ExternalProviderCreateDto
        {
            ProviderKey = ExternalProviderKeys.Fanart,
            Enabled = true,
            Auth = new Dictionary<string, string?> { ["apiKey"] = "fanart-key" }
        });

        repository.Create(new ExternalProviderCreateDto
        {
            ProviderKey = ExternalProviderKeys.Igdb,
            Enabled = true,
            Auth = new Dictionary<string, string?>
            {
                ["clientId"] = "igdb-client-id",
                ["clientSecret"] = "igdb-client-secret"
            }
        });

        repository.Create(new ExternalProviderCreateDto
        {
            ProviderKey = ExternalProviderKeys.Jikan,
            Enabled = true
        });

        repository.Create(new ExternalProviderCreateDto
        {
            ProviderKey = ExternalProviderKeys.GoogleBooks,
            Enabled = true,
            Auth = new Dictionary<string, string?> { ["apiKey"] = "google-books-key" }
        });

        repository.Create(new ExternalProviderCreateDto
        {
            ProviderKey = ExternalProviderKeys.TheAudioDb,
            Enabled = true,
            Auth = new Dictionary<string, string?> { ["apiKey"] = "theaudiodb-key" }
        });

        repository.Create(new ExternalProviderCreateDto
        {
            ProviderKey = ExternalProviderKeys.ComicVine,
            Enabled = true,
            Auth = new Dictionary<string, string?> { ["apiKey"] = "comicvine-key" }
        });
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }

    private sealed class DeterministicProtectionService : IApiKeyProtectionService
    {
        public string Protect(string plainText)
        {
            if (string.IsNullOrWhiteSpace(plainText))
                return "";
            if (IsProtected(plainText))
                return plainText;
            return "ENC:TEST:" + plainText;
        }

        public string Unprotect(string protectedText)
        {
            if (TryUnprotect(protectedText, out var plainText))
                return plainText;
            return protectedText;
        }

        public bool TryUnprotect(string protectedText, out string plainText)
        {
            plainText = protectedText;
            if (string.IsNullOrWhiteSpace(protectedText))
                return true;
            if (!IsProtected(protectedText))
                return true;
            if (protectedText.StartsWith("ENC:TEST:", StringComparison.Ordinal))
            {
                plainText = protectedText["ENC:TEST:".Length..];
                return true;
            }

            return false;
        }

        public bool IsProtected(string value)
            => !string.IsNullOrWhiteSpace(value) && value.StartsWith("ENC:", StringComparison.Ordinal);
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

    private sealed class TmdbHandler : HttpMessageHandler
    {
        private readonly PosterContractScenario _scenario;

        public TmdbHandler(PosterContractScenario scenario)
        {
            _scenario = scenario;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? new Uri("https://api.themoviedb.org/3/");
            var path = uri.AbsolutePath.ToLowerInvariant();

            if (path.Contains("/search/movie"))
                return Task.FromResult(JsonResponse(MovieSearchPayload()));

            if (path.Contains("/search/tv"))
                return Task.FromResult(JsonResponse(TvSearchPayload()));

            if (path.Contains("/images"))
                return Task.FromResult(JsonResponse(ImagesPayload(path)));

            if (path.Contains("/external_ids"))
                return Task.FromResult(JsonResponse(path.Contains("/tv/700/") ? "{\"tvdb_id\":8700}" : "{\"tvdb_id\":8600}"));

            if (path.Contains("/find/3001"))
                return Task.FromResult(JsonResponse("{\"tv_results\":[{\"id\":501}]}"));

            if (path.Contains("/credits"))
                return Task.FromResult(JsonResponse("{\"cast\":[],\"crew\":[]}"));

            if (path.Contains("/movie/") || path.Contains("/tv/"))
                return Task.FromResult(JsonResponse("{\"title\":\"Mock\",\"name\":\"Mock\",\"overview\":\"Mock\",\"genres\":[],\"vote_average\":0,\"vote_count\":0}"));

            if (uri.Host.Contains("image.tmdb.org", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = ImageBytesForPath(path);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private string MovieSearchPayload()
        {
            var payload = _scenario switch
            {
                PosterContractScenario.MovieTmdbHit => "{\"results\":[{\"id\":100,\"title\":\"The Matrix\",\"original_title\":\"The Matrix\",\"poster_path\":\"/tmdb/matrix-search.jpg\",\"release_date\":\"1999-03-31\",\"original_language\":\"en\"}]}",
                PosterContractScenario.MovieTmdbFallbackFanart => "{\"results\":[{\"id\":200,\"title\":\"Fallback Movie\",\"original_title\":\"Fallback Movie\",\"poster_path\":\"/tmdb/fallback-empty.jpg\",\"release_date\":\"2001-01-01\",\"original_language\":\"en\"}]}",
                PosterContractScenario.CacheReuseMovie => "{\"results\":[{\"id\":900,\"title\":\"Cache Movie\",\"original_title\":\"Cache Movie\",\"poster_path\":\"/tmdb/cache-900.jpg\",\"release_date\":\"2022-01-01\",\"original_language\":\"en\"}]}",
                _ => "{\"results\":[]}"
            };
            return payload;
        }

        private string TvSearchPayload()
        {
            var payload = _scenario switch
            {
                PosterContractScenario.SeriesTvmazeMissTmdb => "{\"results\":[{\"id\":600,\"name\":\"Show Tmdb Hit\",\"original_name\":\"Show Tmdb Hit\",\"poster_path\":\"/tmdb/series-600.jpg\",\"first_air_date\":\"2018-01-01\",\"original_language\":\"en\"}]}",
                PosterContractScenario.SeriesTmdbFallbackFanart => "{\"results\":[{\"id\":700,\"name\":\"Show Fanart\",\"original_name\":\"Show Fanart\",\"poster_path\":null,\"first_air_date\":\"2019-01-01\",\"original_language\":\"en\"}]}",
                PosterContractScenario.EmissionAmbiguousTmdbFallback => "{\"results\":[{\"id\":800,\"name\":\"Emission TF1\",\"original_name\":\"Emission TF1\",\"poster_path\":\"/tmdb/emission-800.jpg\",\"first_air_date\":\"2024-01-01\",\"original_language\":\"fr\"}]}",
                _ => "{\"results\":[]}"
            };
            return payload;
        }

        private string ImagesPayload(string path)
        {
            if (path.Contains("/movie/100/images"))
                return "{\"posters\":[{\"file_path\":\"/tmdb/matrix-pref.jpg\",\"iso_639_1\":\"fr\",\"vote_average\":8,\"vote_count\":100,\"width\":1000,\"height\":1500}],\"backdrops\":[]}";
            if (path.Contains("/movie/200/images"))
                return "{\"posters\":[],\"backdrops\":[]}";
            if (path.Contains("/movie/900/images"))
                return "{\"posters\":[{\"file_path\":\"/tmdb/cache-900.jpg\",\"iso_639_1\":\"en\",\"vote_average\":8,\"vote_count\":10,\"width\":1000,\"height\":1500}],\"backdrops\":[]}";
            if (path.Contains("/tv/600/images"))
                return "{\"posters\":[{\"file_path\":\"/tmdb/series-600-pref.jpg\",\"iso_639_1\":\"en\",\"vote_average\":8,\"vote_count\":10,\"width\":1000,\"height\":1500}],\"backdrops\":[]}";
            if (path.Contains("/tv/800/images"))
                return "{\"posters\":[{\"file_path\":\"/tmdb/emission-800-pref.jpg\",\"iso_639_1\":\"fr\",\"vote_average\":8,\"vote_count\":10,\"width\":1000,\"height\":1500}],\"backdrops\":[]}";
            return "{\"posters\":[],\"backdrops\":[]}";
        }

        private byte[] ImageBytesForPath(string path)
        {
            if (_scenario == PosterContractScenario.MovieTmdbFallbackFanart && path.Contains("fallback-empty"))
                return Array.Empty<byte>();

            if (_scenario == PosterContractScenario.SeriesTmdbFallbackFanart && path.Contains("/w500") && path.Contains("700"))
                return Array.Empty<byte>();

            return Encoding.UTF8.GetBytes("image-bytes");
        }

        private static HttpResponseMessage JsonResponse(string json)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
    }

    private sealed class FanartHandler : HttpMessageHandler
    {
        private readonly PosterContractScenario _scenario;

        public FanartHandler(PosterContractScenario scenario)
        {
            _scenario = scenario;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? new Uri("https://webservice.fanart.tv/v3/");
            var path = uri.AbsolutePath.ToLowerInvariant();

            if (_scenario == PosterContractScenario.MovieTmdbFallbackFanart && path.Contains("/movies/200"))
                return Task.FromResult(JsonResponse("{\"movieposter\":[{\"url\":\"https://img.fanart.tv/fallback-movie.jpg\",\"lang\":\"en\",\"likes\":\"10\"}]}"));

            if (_scenario == PosterContractScenario.SeriesTmdbFallbackFanart && path.Contains("/tv/8700"))
                return Task.FromResult(JsonResponse("{\"tvposter\":[{\"url\":\"https://img.fanart.tv/series-fallback.jpg\",\"lang\":\"en\",\"likes\":\"10\"}]}"));

            if (uri.Host.Contains("img.fanart.tv", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("fanart-image"))
                });
            }

            return Task.FromResult(JsonResponse("{}"));
        }

        private static HttpResponseMessage JsonResponse(string json)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
    }

    private sealed class IgdbHandler : HttpMessageHandler
    {
        private readonly PosterContractScenario _scenario;

        public IgdbHandler(PosterContractScenario scenario)
        {
            _scenario = scenario;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? new Uri("https://api.igdb.com/v4/games");
            var path = uri.AbsolutePath.ToLowerInvariant();

            if (uri.Host.Contains("id.twitch.tv", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("{\"access_token\":\"token\",\"expires_in\":3600}");
            }

            if (uri.Host.Contains("api.igdb.com", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/games", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                if (body.Contains("where id =", StringComparison.OrdinalIgnoreCase))
                    return JsonResponse("[{\"id\":901,\"name\":\"Game Hit\",\"summary\":\"Mock\",\"genres\":[],\"total_rating\":0,\"total_rating_count\":0}]");

                if (_scenario == PosterContractScenario.GameIgdbHit)
                    return JsonResponse("[{\"id\":901,\"name\":\"Game Hit\",\"first_release_date\":1640995200,\"cover\":{\"url\":\"//images.igdb.com/igdb/upload/t_thumb/game901.jpg\"}}]");

                if (_scenario == PosterContractScenario.GameIgdbMiss)
                    return JsonResponse("[]");
            }

            if (uri.Host.Contains("images.igdb.com", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("igdb-image"))
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonResponse(string json)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
    }

    private sealed class TvMazeHandler : HttpMessageHandler
    {
        private readonly PosterContractScenario _scenario;

        public TvMazeHandler(PosterContractScenario scenario)
        {
            _scenario = scenario;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? new Uri("https://api.tvmaze.com/");
            var path = uri.AbsolutePath.ToLowerInvariant();
            var query = Uri.UnescapeDataString(uri.Query);

            if (path.Contains("/search/shows"))
            {
                if (_scenario == PosterContractScenario.SeriesTvmazeHit)
                    return Task.FromResult(JsonResponse("[{\"show\":{\"id\":3001,\"name\":\"Show Tvmaze Hit\",\"premiered\":\"2015-01-01\",\"externals\":{\"imdb\":\"tt03001\",\"thetvdb\":3001},\"image\":{\"medium\":\"https://static.tvmaze.com/medium-3001.jpg\",\"original\":\"https://static.tvmaze.com/original-3001.jpg\"}}}]"));

                if (_scenario == PosterContractScenario.EmissionAmbiguousTmdbFallback)
                    return Task.FromResult(JsonResponse("[{\"show\":{\"id\":4010,\"name\":\"Random Program\",\"premiered\":\"2000-01-01\",\"externals\":{\"imdb\":\"tt04010\",\"thetvdb\":4010},\"image\":{\"medium\":\"https://static.tvmaze.com/medium-4010.jpg\",\"original\":\"https://static.tvmaze.com/original-4010.jpg\"}}}]"));

                if (query.Contains("Show", StringComparison.OrdinalIgnoreCase) || query.Contains("TF1", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(JsonResponse("[]"));
            }

            if (path.Contains("/shows/3001"))
                return Task.FromResult(JsonResponse("{\"id\":3001,\"name\":\"Show Tvmaze Hit\",\"premiered\":\"2015-01-01\",\"externals\":{\"imdb\":\"tt03001\",\"thetvdb\":3001},\"image\":{\"medium\":\"https://static.tvmaze.com/medium-3001.jpg\",\"original\":\"https://static.tvmaze.com/original-3001.jpg\"}}"));

            if (uri.Host.Contains("static.tvmaze.com", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("tvmaze-image"))
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

    private sealed class JikanHandler : HttpMessageHandler
    {
        private readonly PosterContractScenario _scenario;

        public JikanHandler(PosterContractScenario scenario)
        {
            _scenario = scenario;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? new Uri("https://api.jikan.moe/v4/");
            var path = uri.AbsolutePath.ToLowerInvariant();

            if (path.Contains("/anime"))
            {
                if (_scenario == PosterContractScenario.AnimeJikanHit)
                {
                    return Task.FromResult(JsonResponse("{\"data\":[{\"mal_id\":1500,\"title\":\"Naruto\",\"title_english\":\"Naruto\",\"year\":2002,\"synopsis\":\"Mock anime synopsis\",\"score\":8.5,\"scored_by\":1000,\"url\":\"https://myanimelist.net/anime/1500\",\"images\":{\"jpg\":{\"large_image_url\":\"https://cdn.jikan.moe/images/anime/naruto.jpg\"}},\"genres\":[{\"name\":\"Action\"},{\"name\":\"Adventure\"}]}]}"));
                }
                return Task.FromResult(JsonResponse("{\"data\":[]}"));
            }

            if (uri.Host.Contains("cdn.jikan.moe", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("jikan-image"))
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

    private sealed class TheAudioDbHandler : HttpMessageHandler
    {
        private readonly PosterContractScenario _scenario;

        public TheAudioDbHandler(PosterContractScenario scenario)
        {
            _scenario = scenario;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? new Uri("https://www.theaudiodb.com/api/v1/json/");
            var path = uri.AbsolutePath.ToLowerInvariant();

            if (path.Contains("searchtrack.php"))
            {
                if (_scenario == PosterContractScenario.AudioAudioDbHit)
                    return Task.FromResult(JsonResponse("{\"track\":[{\"idTrack\":\"4500\",\"strTrack\":\"Around the World\",\"strArtist\":\"Daft Punk\",\"strDescriptionEN\":\"Mock audio description\",\"strGenre\":\"Electronic\",\"intYearReleased\":\"1997\",\"strTrackThumb\":\"https://www.theaudiodb.com/images/media/track/around.jpg\",\"intScore\":\"8.0\"}]}"));
                return Task.FromResult(JsonResponse("{\"track\":null}"));
            }

            if (path.Contains("searchalbum.php"))
            {
                if (_scenario == PosterContractScenario.AudioAudioDbHit)
                    return Task.FromResult(JsonResponse("{\"album\":[{\"idAlbum\":\"5500\",\"strAlbum\":\"Around the World\",\"strArtist\":\"Daft Punk\",\"strDescriptionEN\":\"Mock audio album\",\"strGenre\":\"Electronic\",\"intYearReleased\":\"1997\",\"strAlbumThumb\":\"https://www.theaudiodb.com/images/media/album/around.jpg\",\"intScore\":\"7.8\"}]}"));
                return Task.FromResult(JsonResponse("{\"album\":null}"));
            }

            if (uri.Host.Contains("theaudiodb.com", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".jpg", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audiodb-image"))
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

    private sealed class GoogleBooksHandler : HttpMessageHandler
    {
        private readonly PosterContractScenario _scenario;

        public GoogleBooksHandler(PosterContractScenario scenario)
        {
            _scenario = scenario;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? new Uri("https://www.googleapis.com/books/v1/");
            var path = uri.AbsolutePath.ToLowerInvariant();

            if (path.Contains("/volumes"))
            {
                if (_scenario == PosterContractScenario.BookGoogleBooksHit)
                    return Task.FromResult(JsonResponse("{\"items\":[{\"id\":\"book-900\",\"volumeInfo\":{\"title\":\"Clean Architecture\",\"description\":\"Mock book description\",\"publishedDate\":\"2017-09-20\",\"categories\":[\"Software Engineering\"],\"averageRating\":4.2,\"ratingsCount\":200,\"imageLinks\":{\"thumbnail\":\"https://books.google.com/mock-book.jpg\"},\"industryIdentifiers\":[{\"identifier\":\"9780134494166\"}]}}]}"));
                return Task.FromResult(JsonResponse("{\"items\":[]}"));
            }

            if (uri.Host.Contains("books.google.com", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("googlebooks-image"))
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

    private sealed class ComicVineHandler : HttpMessageHandler
    {
        private readonly PosterContractScenario _scenario;

        public ComicVineHandler(PosterContractScenario scenario)
        {
            _scenario = scenario;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? new Uri("https://comicvine.gamespot.com/api/");
            var path = uri.AbsolutePath.ToLowerInvariant();

            if (path.Contains("/search"))
            {
                if (_scenario == PosterContractScenario.ComicComicVineHit)
                    return Task.FromResult(JsonResponse("{\"results\":[{\"id\":9900,\"name\":\"Batman #1\",\"deck\":\"Mock comic deck\",\"description\":\"<p>Mock comic description</p>\",\"cover_date\":\"2020-10-01\",\"site_detail_url\":\"https://comicvine.gamespot.com/batman-1\",\"image\":{\"original_url\":\"https://comicvine.gamespot.com/mock-comic.jpg\"}}]}"));
                return Task.FromResult(JsonResponse("{\"results\":[]}"));
            }

            if (uri.Host.Contains("comicvine.gamespot.com", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".jpg", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("comicvine-image"))
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
}
