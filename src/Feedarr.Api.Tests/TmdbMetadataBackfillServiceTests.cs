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
using Feedarr.Api.Services.Metadata;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Tmdb;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class TmdbMetadataBackfillServiceTests
{
    [Fact]
    public async Task BackfillMissingTmdbMetadataAsync_PropagatesLocalDonor_WithoutTmdbCall()
    {
        using var ctx = new MetadataBackfillContext();
        ctx.CreateRelease(
            title: "Donor Movie",
            titleClean: "Donor Movie",
            mediaType: "movie",
            year: 1999,
            tmdbId: 100,
            posterFile: "tmdb-100.jpg",
            extProvider: "tmdb",
            extProviderId: "100",
            extOverview: "Local donor overview",
            extUpdatedAtTs: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60);
        var targetId = ctx.CreateRelease(
            title: "Target Movie",
            titleClean: "Target Movie",
            mediaType: "movie",
            year: 2000,
            tmdbId: 100,
            posterFile: "tmdb-100.jpg");

        var result = await ctx.Service.BackfillMissingTmdbMetadataAsync(200, CancellationToken.None);

        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Eligible);
        Assert.Equal(1, result.LocalPropagated);
        Assert.Equal(0, result.TmdbRefreshed);
        Assert.Equal(0, result.UniqueTmdbKeysRefreshed);
        Assert.Equal(0, result.Errors);
        Assert.Equal(0, ctx.TmdbHandler.MovieDetailsCalls);
        Assert.Equal(0, ctx.TmdbHandler.TvDetailsCalls);

        var target = ctx.Releases.GetForPoster(targetId);
        Assert.NotNull(target);
        Assert.Equal("tmdb", target!.ExtProvider);
        Assert.Equal("Local donor overview", target.ExtOverview);
        Assert.True((target.ExtUpdatedAtTs ?? 0) > 0);
    }

    [Fact]
    public async Task BackfillMissingTmdbMetadataAsync_FallbackTmdb_DeduplicatesByTmdbAndType()
    {
        using var ctx = new MetadataBackfillContext();
        var release1 = ctx.CreateRelease(
            title: "Fallback One",
            titleClean: "Fallback One",
            mediaType: "movie",
            year: 2001,
            tmdbId: 200,
            posterFile: "tmdb-200.jpg");
        var release2 = ctx.CreateRelease(
            title: "Fallback Two",
            titleClean: "Fallback Two",
            mediaType: "movie",
            year: 2002,
            tmdbId: 200,
            posterFile: "tmdb-200.jpg");

        var result = await ctx.Service.BackfillMissingTmdbMetadataAsync(200, CancellationToken.None);

        Assert.Equal(2, result.Scanned);
        Assert.Equal(2, result.Eligible);
        Assert.Equal(2, result.TmdbRefreshed);
        Assert.Equal(1, result.UniqueTmdbKeysRefreshed);
        Assert.Equal(0, result.LocalPropagated);
        Assert.Equal(0, result.Errors);
        Assert.Equal(1, ctx.TmdbHandler.MovieDetailsCalls);
        Assert.Equal(1, ctx.TmdbHandler.MovieCreditsCalls);

        var row1 = ctx.Releases.GetForPoster(release1);
        var row2 = ctx.Releases.GetForPoster(release2);
        Assert.NotNull(row1);
        Assert.NotNull(row2);
        Assert.Equal("tmdb", row1!.ExtProvider);
        Assert.Equal("tmdb", row2!.ExtProvider);
        Assert.Equal("TMDB Movie 200 overview", row1.ExtOverview);
        Assert.Equal("TMDB Movie 200 overview", row2.ExtOverview);
        Assert.True((row1.ExtUpdatedAtTs ?? 0) > 0);
        Assert.True((row2.ExtUpdatedAtTs ?? 0) > 0);
    }

    [Fact]
    public async Task BackfillMissingTmdbMetadataAsync_SkipsReleaseAlreadyEnriched()
    {
        using var ctx = new MetadataBackfillContext();
        var existingTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 300;
        var releaseId = ctx.CreateRelease(
            title: "Already Enriched",
            titleClean: "Already Enriched",
            mediaType: "movie",
            year: 2010,
            tmdbId: 300,
            posterFile: "tmdb-300.jpg",
            extProvider: "tmdb",
            extProviderId: "300",
            extOverview: "Existing overview",
            extUpdatedAtTs: existingTs);

        var result = await ctx.Service.BackfillMissingTmdbMetadataAsync(200, CancellationToken.None);

        Assert.Equal(0, result.Scanned);
        Assert.Equal(0, result.Eligible);
        Assert.Equal(0, result.Processed);
        Assert.Equal(0, ctx.TmdbHandler.MovieDetailsCalls);

        var row = ctx.Releases.GetForPoster(releaseId);
        Assert.NotNull(row);
        Assert.Equal(existingTs, row!.ExtUpdatedAtTs);
        Assert.Equal("Existing overview", row.ExtOverview);
    }

    [Fact]
    public async Task BackfillMissingTmdbMetadataAsync_SkipsReleaseWithoutTmdbId()
    {
        using var ctx = new MetadataBackfillContext();
        var releaseId = ctx.CreateRelease(
            title: "No Tmdb Id",
            titleClean: "No Tmdb Id",
            mediaType: "movie",
            year: 2011,
            tmdbId: null,
            posterFile: "no-tmdb.jpg");

        var result = await ctx.Service.BackfillMissingTmdbMetadataAsync(200, CancellationToken.None);

        Assert.Equal(0, result.Scanned);
        Assert.Equal(0, result.Processed);
        Assert.Equal(0, ctx.TmdbHandler.MovieDetailsCalls);

        var row = ctx.Releases.GetForPoster(releaseId);
        Assert.NotNull(row);
        Assert.Null(row!.ExtUpdatedAtTs);
    }

    [Fact]
    public async Task BackfillMissingTmdbMetadataAsync_DoesNotCrossPropagateBetweenMovieAndSeries()
    {
        using var ctx = new MetadataBackfillContext();
        ctx.CreateRelease(
            title: "Movie Donor",
            titleClean: "Movie Donor",
            mediaType: "movie",
            year: 2012,
            tmdbId: 100,
            posterFile: "tmdb-100.jpg",
            extProvider: "tmdb",
            extProviderId: "100",
            extOverview: "Movie-only overview",
            extUpdatedAtTs: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 120);
        var seriesTargetId = ctx.CreateRelease(
            title: "Series Target",
            titleClean: "Series Target",
            mediaType: "series",
            year: 2012,
            tmdbId: 100,
            posterFile: "tmdb-100.jpg");

        var result = await ctx.Service.BackfillMissingTmdbMetadataAsync(200, CancellationToken.None);

        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Eligible);
        Assert.Equal(0, result.LocalPropagated);
        Assert.Equal(0, result.TmdbRefreshed);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, ctx.TmdbHandler.MovieDetailsCalls);
        Assert.Equal(1, ctx.TmdbHandler.TvDetailsCalls);

        var target = ctx.Releases.GetForPoster(seriesTargetId);
        Assert.NotNull(target);
        Assert.Null(target!.ExtUpdatedAtTs);
        Assert.Null(target.ExtOverview);
    }

    [Fact]
    public async Task BackfillMissingTmdbMetadataAsync_ReturnsCoherentStats()
    {
        using var ctx = new MetadataBackfillContext();
        ctx.CreateRelease(
            title: "Local Donor",
            titleClean: "Local Donor",
            mediaType: "movie",
            year: 2015,
            tmdbId: 100,
            posterFile: "tmdb-100.jpg",
            extProvider: "tmdb",
            extProviderId: "100",
            extOverview: "Local metadata",
            extUpdatedAtTs: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60);
        ctx.CreateRelease(
            title: "Local Target",
            titleClean: "Local Target",
            mediaType: "movie",
            year: 2016,
            tmdbId: 100,
            posterFile: "tmdb-100.jpg");
        ctx.CreateRelease(
            title: "Fallback Target A",
            titleClean: "Fallback Target A",
            mediaType: "movie",
            year: 2017,
            tmdbId: 200,
            posterFile: "tmdb-200.jpg");
        ctx.CreateRelease(
            title: "Fallback Target B",
            titleClean: "Fallback Target B",
            mediaType: "movie",
            year: 2018,
            tmdbId: 200,
            posterFile: "tmdb-200.jpg");
        ctx.CreateRelease(
            title: "Already Done",
            titleClean: "Already Done",
            mediaType: "movie",
            year: 2019,
            tmdbId: 300,
            posterFile: "tmdb-300.jpg",
            extProvider: "tmdb",
            extProviderId: "300",
            extOverview: "already present",
            extUpdatedAtTs: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 500);
        ctx.CreateRelease(
            title: "No Id",
            titleClean: "No Id",
            mediaType: "movie",
            year: 2020,
            tmdbId: null,
            posterFile: "no-id.jpg");

        var result = await ctx.Service.BackfillMissingTmdbMetadataAsync(200, CancellationToken.None);

        Assert.Equal(3, result.Scanned);
        Assert.Equal(3, result.Eligible);
        Assert.Equal(3, result.Processed);
        Assert.Equal(1, result.LocalPropagated);
        Assert.Equal(2, result.TmdbRefreshed);
        Assert.Equal(1, result.UniqueTmdbKeysRefreshed);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.Errors);
        Assert.Equal(1, ctx.TmdbHandler.MovieDetailsCalls);
        Assert.Equal(1, ctx.TmdbHandler.MovieCreditsCalls);
    }

    private sealed class MetadataBackfillContext : IDisposable
    {
        private readonly TestWorkspace _workspace;

        public MetadataBackfillContext()
        {
            _workspace = new TestWorkspace();

            var options = OptionsFactory.Create(new AppOptions
            {
                DataDir = _workspace.DataDir,
                DbFileName = "feedarr.db"
            });

            Db = new Db(options);
            new MigrationsRunner(Db, NullLogger<MigrationsRunner>.Instance).Run();

            var protection = new PassthroughProtectionService();
            var settings = new SettingsRepository(Db, protection, NullLogger<SettingsRepository>.Instance);
            var stats = new ProviderStatsService(new StatsRepository(Db, new MemoryCache(new MemoryCacheOptions())));
            var registry = new ExternalProviderRegistry();
            var instances = new ExternalProviderInstanceRepository(
                Db,
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

            Releases = new ReleaseRepository(Db, new TitleParser(), new UnifiedCategoryResolver());
            TmdbHandler = new BackfillTmdbHandler();
            var tmdb = new TmdbClient(new HttpClient(TmdbHandler), settings, stats, activeResolver);
            Service = new TmdbMetadataBackfillService(Releases, tmdb, NullLogger<TmdbMetadataBackfillService>.Instance);

            using var conn = Db.Open();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SourceId = conn.ExecuteScalar<long>(
                """
                INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
                VALUES('test', 1, 'http://localhost:9117/api', 'key', 'query', @ts, @ts);
                SELECT last_insert_rowid();
                """,
                new { ts });
        }

        public Db Db { get; }
        public ReleaseRepository Releases { get; }
        public TmdbMetadataBackfillService Service { get; }
        public BackfillTmdbHandler TmdbHandler { get; }
        public long SourceId { get; }

        public long CreateRelease(
            string title,
            string titleClean,
            string mediaType,
            int? year,
            int? tmdbId,
            string? posterFile,
            string? extProvider = null,
            string? extProviderId = null,
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
                  ext_provider,
                  ext_provider_id,
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
                  @unifiedCategory,
                  @mediaType,
                  @tmdbId,
                  @posterFile,
                  @extProvider,
                  @extProviderId,
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
                    titleClean,
                    year,
                    unifiedCategory = ToUnifiedCategory(mediaType),
                    mediaType,
                    tmdbId,
                    posterFile,
                    extProvider,
                    extProviderId,
                    extOverview,
                    extUpdatedAtTs,
                    ts
                });
        }

        public void Dispose()
        {
            _workspace.Dispose();
        }

        private static string ToUnifiedCategory(string mediaType)
            => string.Equals(mediaType, "series", StringComparison.OrdinalIgnoreCase)
                ? "Serie"
                : "Film";
    }

    private sealed class BackfillTmdbHandler : HttpMessageHandler
    {
        public int MovieDetailsCalls { get; private set; }
        public int MovieCreditsCalls { get; private set; }
        public int TvDetailsCalls { get; private set; }
        public int TvCreditsCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri;
            var segments = (uri?.AbsolutePath ?? string.Empty)
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length < 3)
                return Task.FromResult(NotFound());

            var mediaKind = segments[1];
            if (!int.TryParse(segments[2], out var externalId))
                return Task.FromResult(NotFound());
            var isCredits = segments.Length > 3 && string.Equals(segments[3], "credits", StringComparison.OrdinalIgnoreCase);

            if (string.Equals(mediaKind, "movie", StringComparison.OrdinalIgnoreCase))
            {
                if (isCredits)
                {
                    MovieCreditsCalls++;
                    return Task.FromResult(JsonResponse("{\"cast\":[],\"crew\":[]}"));
                }

                MovieDetailsCalls++;
                if (externalId == 100)
                    return Task.FromResult(JsonResponse("{\"title\":\"Movie 100\",\"overview\":\"TMDB Movie 100 overview\",\"genres\":[],\"runtime\":120,\"vote_average\":8.0,\"vote_count\":100,\"release_date\":\"2000-01-01\",\"tagline\":\"tag\"}"));
                if (externalId == 200)
                    return Task.FromResult(JsonResponse("{\"title\":\"Movie 200\",\"overview\":\"TMDB Movie 200 overview\",\"genres\":[],\"runtime\":130,\"vote_average\":7.5,\"vote_count\":150,\"release_date\":\"2001-01-01\",\"tagline\":\"tag\"}"));
                if (externalId == 300)
                    return Task.FromResult(JsonResponse("{\"title\":\"Movie 300\",\"overview\":\"TMDB Movie 300 overview\",\"genres\":[],\"runtime\":110,\"vote_average\":7.0,\"vote_count\":90,\"release_date\":\"2002-01-01\",\"tagline\":\"tag\"}"));
                return Task.FromResult(NotFound());
            }

            if (string.Equals(mediaKind, "tv", StringComparison.OrdinalIgnoreCase))
            {
                if (isCredits)
                {
                    TvCreditsCalls++;
                    return Task.FromResult(JsonResponse("{\"cast\":[],\"crew\":[]}"));
                }

                TvDetailsCalls++;
                return Task.FromResult(NotFound());
            }

            return Task.FromResult(NotFound());
        }

        private static HttpResponseMessage JsonResponse(string json)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

        private static HttpResponseMessage NotFound()
            => new(HttpStatusCode.NotFound);
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
