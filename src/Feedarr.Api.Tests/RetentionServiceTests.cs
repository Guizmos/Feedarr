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

namespace Feedarr.Api.Tests;

public sealed class RetentionServiceTests
{
    [Fact]
    public void ApplyRetention_WhenDeleteThrows_KeepsFileAndCountsFailedDelete()
    {
        using var context = new RetentionTestContext();
        context.CreateRelease("newer-guid", 200, "newer.jpg");
        var purgedId = context.CreateRelease("older-guid", 100, "locked.jpg");
        var lockedPath = context.CreatePosterFile("locked.jpg");

        using var handle = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var (result, postersPurged, failedDeletes) = context.Service.ApplyRetention(context.SourceId, perCatLimit: 1, globalLimit: 0);

        Assert.Single(result.DeletedReleaseIds, purgedId);
        Assert.Equal(0, postersPurged);
        Assert.Equal(1, failedDeletes);
        Assert.True(File.Exists(lockedPath));
    }

    [Fact]
    public void ApplyRetention_WhenPosterFileIsMissing_TreatsCleanupAsSuccess()
    {
        using var context = new RetentionTestContext();
        var purgedId = context.CreateRelease("older-guid", 100, "missing.jpg");
        context.CreateRelease("newer-guid", 200, "newer.jpg");

        var (result, postersPurged, failedDeletes) = context.Service.ApplyRetention(context.SourceId, perCatLimit: 1, globalLimit: 0);

        Assert.Single(result.DeletedReleaseIds, purgedId);
        Assert.Equal(1, postersPurged);
        Assert.Equal(0, failedDeletes);
        Assert.Equal(0, context.Releases.GetPosterReferenceCount("missing.jpg"));
    }

    private sealed class RetentionTestContext : IDisposable
    {
        private readonly TestWorkspace _workspace;
        private readonly TestWebHostEnvironment _environment;
        private readonly Microsoft.Extensions.Options.IOptions<AppOptions> _options;

        public RetentionTestContext()
        {
            _workspace = new TestWorkspace();
            _environment = new TestWebHostEnvironment(_workspace.RootDir);
            _options = OptionsFactory.Create(new AppOptions
            {
                DataDir = _workspace.DataDir,
                DbFileName = "feedarr.db"
            });

            Db = new Db(_options);
            new MigrationsRunner(Db, NullLogger<MigrationsRunner>.Instance).Run();

            Releases = new ReleaseRepository(Db, new TitleParser(), new UnifiedCategoryResolver());
            Activity = new ActivityRepository(Db, new BadgeSignal());

            var protection = new PassthroughProtectionService();
            var settings = new SettingsRepository(Db, protection, NullLogger<SettingsRepository>.Instance);
            var registry = new ExternalProviderRegistry();
            var instances = new ExternalProviderInstanceRepository(
                Db,
                settings,
                protection,
                registry,
                NullLogger<ExternalProviderInstanceRepository>.Instance);
            var resolver = new ActiveExternalProviderConfigResolver(
                instances,
                registry,
                NullLogger<ActiveExternalProviderConfigResolver>.Instance);

            Posters = new PosterFetchService(
                Releases,
                Activity,
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
                null!,
                new PosterMatchCacheService(Db),
                _options,
                _environment,
                new PosterMatchingOrchestrator(
                    new VideoMatchingStrategy(),
                    new GameMatchingStrategy(),
                    new AnimeMatchingStrategy(),
                    new AudioMatchingStrategy(),
                    new GenericMatchingStrategy()),
                resolver,
                NullLogger<PosterFetchService>.Instance);

            Service = new RetentionService(Releases, Posters, NullLogger<RetentionService>.Instance);

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
        public ActivityRepository Activity { get; }
        public PosterFetchService Posters { get; }
        public RetentionService Service { get; }
        public long SourceId { get; }

        public long CreateRelease(string guid, long publishedAtTs, string posterFile)
        {
            using var conn = Db.Open();
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
                  poster_updated_at_ts
                )
                VALUES(
                  @sourceId,
                  @guid,
                  @guid,
                  @publishedAtTs,
                  @publishedAtTs,
                  @guid,
                  2024,
                  'Film',
                  'movie',
                  @posterFile,
                  @publishedAtTs
                );
                SELECT last_insert_rowid();
                """,
                new { sourceId = SourceId, guid, publishedAtTs, posterFile });
        }

        public string CreatePosterFile(string fileName)
        {
            Directory.CreateDirectory(Posters.PostersDirPath);
            var path = Path.Combine(Posters.PostersDirPath, fileName);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
            return path;
        }

        public void Dispose()
        {
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
