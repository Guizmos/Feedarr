using System.Text.Json;
using Dapper;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Torznab;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class TrackerPageUrlTests
{
    private static readonly JsonSerializerOptions HttpJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Qualifier_AcceptsPublicHttpsDetailPage()
    {
        Assert.Equal(
            "https://tracker.example/releases/123",
            TrackerPageUrlQualifier.Qualify(" https://tracker.example/releases/123 "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("release comments only")]
    [InlineData("/releases/123")]
    [InlineData("magnet:?xt=urn:btih:123")]
    [InlineData("https://tracker.example/files/release.torrent")]
    [InlineData("https://tracker.example/download/123")]
    [InlineData("https://jackett.example/api/v2.0/indexers/all/results/torznab/api?t=search")]
    [InlineData("https://prowlarr.example/12/api?t=search")]
    [InlineData("https://tracker.example/search?q=release")]
    [InlineData("https://tracker.example/releases/123?apikey=secret")]
    [InlineData("https://tracker.example/releases/123?access_token=secret")]
    [InlineData("https://tracker.example/releases/123?session=secret")]
    [InlineData("https://tracker.example/releases/***")]
    [InlineData("https://user:password@tracker.example/releases/123")]
    [InlineData("http://localhost/releases/123")]
    [InlineData("http://127.0.0.1/releases/123")]
    [InlineData("http://192.168.1.2/releases/123")]
    [InlineData("http://tracker.local/releases/123")]
    [InlineData("https://www.themoviedb.org/movie/123")]
    public void Qualifier_RejectsUnsafeOrNonDetailUrls(string? candidate)
    {
        Assert.Null(TrackerPageUrlQualifier.Qualify(candidate));
    }

    [Fact]
    public void Parser_UsesOnlyQualifiedCommentsAndPreservesExistingUrlFields()
    {
        const string xml = """
            <rss version="2.0"><channel>
              <item>
                <title>Release</title>
                <guid>https://tracker.example/from-guid</guid>
                <link>https://tracker.example/ambiguous-link</link>
                <comments>https://tracker.example/releases/123</comments>
                <enclosure url="https://downloads.example/release.torrent" />
              </item>
            </channel></rss>
            """;

        var item = Assert.Single(new TorznabRssParser().Parse(xml));

        Assert.Equal("https://tracker.example/releases/123", item.TrackerPageUrl);
        Assert.Equal("https://tracker.example/from-guid", item.Guid);
        Assert.Equal("https://tracker.example/ambiguous-link", item.Link);
        Assert.Equal("https://downloads.example/release.torrent", item.DownloadUrl);
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("https://tracker.example/download/123")]
    [InlineData("https://tracker.example/releases/123?token=secret")]
    public void Parser_RejectsUnsafeCommentsAndNeverFallsBackToLink(string comments)
    {
        var xml = $"""
            <rss version="2.0"><channel><item>
              <title>Release</title>
              <guid>https://tracker.example/from-guid</guid>
              <link>https://tracker.example/from-link</link>
              <comments>{comments}</comments>
            </item></channel></rss>
            """;

        var item = Assert.Single(new TorznabRssParser().Parse(xml));
        Assert.Null(item.TrackerPageUrl);
        Assert.Equal("https://tracker.example/from-link", item.Link);
        Assert.Equal("https://tracker.example/from-link", item.DownloadUrl);
    }

    [Fact]
    public void Repository_UpsertPreservesThenReplacesOnlyWithQualifiedUrl()
    {
        using var context = new TestContext();
        var repository = context.CreateRepository();

        repository.UpsertMany(context.SourceId, "TEST", [CreateItem("https://tracker.example/releases/old")]);
        repository.UpsertMany(context.SourceId, "TEST", [CreateItem(null)]);
        Assert.Equal("https://tracker.example/releases/old", context.ReadTrackerPageUrl());

        repository.UpsertMany(context.SourceId, "TEST", [CreateItem("https://tracker.example/releases/123?apikey=secret")]);
        Assert.Equal("https://tracker.example/releases/old", context.ReadTrackerPageUrl());

        repository.UpsertMany(context.SourceId, "TEST", [CreateItem("https://tracker.example/releases/new")]);
        Assert.Equal("https://tracker.example/releases/new", context.ReadTrackerPageUrl());
    }

    [Fact]
    public void Migration_AddsNullableColumnWithoutChangingExistingRelease()
    {
        using var context = new TestContext(runMigrations: false);
        using var conn = context.Db.Open();
        conn.Execute("CREATE TABLE releases(id INTEGER PRIMARY KEY, title TEXT NOT NULL);");
        conn.Execute("INSERT INTO releases(id, title) VALUES (1, 'Existing');");
        var migrationPath = Path.Combine(AppContext.BaseDirectory, "Data", "Migrations", "0054_release_tracker_page_url.sql");

        conn.Execute(File.ReadAllText(migrationPath));

        Assert.Equal("Existing", conn.ExecuteScalar<string>("SELECT title FROM releases WHERE id = 1;"));
        Assert.Null(conn.ExecuteScalar<string?>("SELECT tracker_page_url FROM releases WHERE id = 1;"));
    }

    [Fact]
    public void FeedEndpointsExposeCamelCaseTrackerPageUrlAndKeepDownloadPath()
    {
        using var context = new TestContext();
        context.CreateRepository().UpsertMany(
            context.SourceId,
            "TEST",
            [CreateItem("https://tracker.example/releases/123", DateTimeOffset.UtcNow.ToUnixTimeSeconds())]);
        var controller = context.CreateController();

        var legacy = Serialize(Assert.IsType<OkObjectResult>(controller.Latest(context.SourceId, 20, null, null, null, null)).Value);
        var legacyItem = Assert.Single(legacy.RootElement.EnumerateArray().ToList());
        AssertFeedItem(legacyItem);

        var releases = Serialize(Assert.IsType<OkObjectResult>(controller.Releases(20, null, "date", null, null, null, null, null)).Value);
        var releaseItem = Assert.Single(releases.RootElement.GetProperty("items").EnumerateArray().ToList());
        AssertFeedItem(releaseItem);

        var top = Serialize(Assert.IsType<OkObjectResult>(controller.Top(24, 5, 5, "recent", "none", null, context.SourceId, null)).Value);
        var topItem = Assert.Single(top.RootElement.GetProperty("globalTop").EnumerateArray().ToList());
        AssertFeedItem(topItem);
    }

    private static void AssertFeedItem(JsonElement item)
    {
        Assert.Equal("https://tracker.example/releases/123", item.GetProperty("trackerPageUrl").GetString());
        Assert.StartsWith("/api/releases/", item.GetProperty("downloadPath").GetString());
        Assert.False(item.TryGetProperty("TrackerPageUrl", out _));
        Assert.False(item.TryGetProperty("link", out _));
        Assert.False(item.TryGetProperty("downloadUrl", out _));
        Assert.False(item.TryGetProperty("guid", out _));
    }

    private static TorznabItem CreateItem(string? trackerPageUrl, long? publishedAt = 1_700_000_000) => new()
    {
        Guid = "tracker-page-url-test",
        Title = "Tracker Page URL Test",
        PublishedAtTs = publishedAt,
        CategoryId = 2000,
        CategoryIds = [2000],
        TrackerPageUrl = trackerPageUrl,
        Link = "https://downloads.example/ambiguous",
        DownloadUrl = "https://downloads.example/release.torrent"
    };

    private static JsonDocument Serialize(object? value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, HttpJsonOptions));

    private sealed class TestContext : IDisposable
    {
        private readonly string _rootDir;

        public TestContext(bool runMigrations = true)
        {
            _rootDir = Path.Combine(Path.GetTempPath(), "feedarr-tracker-url-tests", Guid.NewGuid().ToString("N"));
            var dataDir = Path.Combine(_rootDir, "data");
            Directory.CreateDirectory(dataDir);
            Db = new Db(OptionsFactory.Create(new AppOptions { DataDir = dataDir, DbFileName = "feedarr.db" }));
            if (!runMigrations)
                return;

            new MigrationsRunner(Db, NullLogger<MigrationsRunner>.Instance).Run();
            using var conn = Db.Open();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SourceId = conn.ExecuteScalar<long>(
                """
                INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
                VALUES ('Tracker Test', 1, 'https://indexer.example/torznab', 'secret', 'query', @now, @now);
                SELECT last_insert_rowid();
                """,
                new { now });
        }

        public Db Db { get; }
        public long SourceId { get; }

        public ReleaseRepository CreateRepository() =>
            new(Db, new TitleParser(), new UnifiedCategoryResolver(), NullLogger<ReleaseRepository>.Instance);

        public FeedController CreateController() =>
            new(Db, new UnifiedCategoryService(), new MemoryCache(new MemoryCacheOptions()));

        public string? ReadTrackerPageUrl()
        {
            using var conn = Db.Open();
            return conn.ExecuteScalar<string?>("SELECT tracker_page_url FROM releases WHERE source_id = @SourceId;", new { SourceId });
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_rootDir))
                    Directory.Delete(_rootDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temporary test data.
            }
        }
    }
}
