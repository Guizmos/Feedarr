using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Backup.Configuration;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.DirectRss;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Sync;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Torznab;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net.Http.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class DirectRssIntegrationTests
{
    [Fact]
    public async Task ApiProtectsUrlsAndTestsWithoutInserting()
    {
        using var c = new Context();
        var api = new DirectRssController(c.Sources, c.Feeds, c.Client);
        var response = Assert.IsType<OkObjectResult>(api.Create(new() { Name = "RSS", Feeds = [new() { Url = "https://example.org/rss?passkey=secret", CategoryKey = "films" }] }));
        var id = JsonSerializer.SerializeToElement(response.Value).GetProperty("id").GetInt64();
        using var db = c.Db.Open();
        Assert.DoesNotContain("secret", db.QuerySingle<string>("SELECT url_encrypted FROM direct_rss_feeds"));
        var listed = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(api.List(id)).Value);
        Assert.DoesNotContain("secret", listed);
        Assert.DoesNotContain("example.org", listed);
        var feed = c.Feeds.List(id).Single();
        var test = Assert.IsType<OkObjectResult>(await api.Test(new() { Id = feed.Id, SourceId = id, CategoryKey = "films" }, default));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(test.Value));
        Assert.Equal(0, db.ExecuteScalar<int>("SELECT COUNT(*) FROM releases"));
        Assert.IsType<OkObjectResult>(api.Update(id, new() { Name = "Updated", Feeds = [new() { Id = feed.Id, CategoryKey = "films" }] }));
        Assert.Equal(feed.Url, c.Feeds.List(id).Single().Url);
        Assert.IsType<BadRequestObjectResult>(api.Update(id, new() { Name = "Invalid", Feeds = [new() { Id = feed.Id, CategoryKey = "unknown" }] }));
    }

    [Fact]
    public async Task SyncMergesBeforeIngestionAllowsPartialFailureAndPreservesReleasesOnFeedRemoval()
    {
        using var c = new Context();
        var id = c.Feeds.Save(null, "RSS", null, [c.Feed("one"), c.Feed("two"), c.Feed("fail")]);
        var result = await c.Sync(id);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(1, result.InsertedNew);
        Assert.Equal(1, result.ItemsCount);
        using var db = c.Db.Open();
        Assert.Equal("films", db.ExecuteScalar<string>("SELECT top_category_key FROM releases"));
        Assert.Equal(2, db.ExecuteScalar<int>("SELECT COUNT(*) FROM direct_rss_provenance"));
        Assert.Single(c.Feeds.List(id), f => f.LastStatus == "error");
        Assert.Equal(0, (await c.Sync(id)).InsertedNew);
        var remaining = c.Feeds.List(id).Where(f => f.Name == "one").ToList();
        remaining[0].Enabled = false;
        c.Feeds.Save(id, "RSS", null, remaining);
        Assert.True((await c.Sync(id)).Ok);
        Assert.Equal(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM releases"));
        c.Feeds.Save(id, "RSS", null, []);
        Assert.True((await c.Sync(id)).Ok);
        Assert.Equal(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM releases"));
        Assert.DoesNotContain(c.Handler.Requests, url => url.Contains("t=") || url.Contains("limit="));
    }

    [Fact]
    public async Task ConflictingNewGuidIsSkippedAndExistingCategoryIsPreserved()
    {
        using var c = new Context();
        var id = c.Feeds.Save(null, "RSS", null, [c.Feed("one"), c.Feed("two", "series")]);
        Assert.Equal(0, (await c.Sync(id)).InsertedNew);
        c.Feeds.Save(id, "RSS", null, [c.Feeds.List(id).First()]);
        Assert.Equal(1, (await c.Sync(id)).InsertedNew);
        c.Feeds.Save(id, "RSS", null, [c.Feed("three", "series")]);
        Assert.Equal(0, (await c.Sync(id)).InsertedNew);
        using var db = c.Db.Open();
        Assert.Equal("films", db.ExecuteScalar<string>("SELECT top_category_key FROM releases"));
        Assert.Equal(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM releases"));
    }

    [Fact]
    public async Task FailureAndSaturationAreTrackedAndMissingReleasesAreRetained()
    {
        using var c = new Context();
        var id = c.Feeds.Save(null, "RSS", null, [c.Feed("fail")]);
        Assert.False((await c.Sync(id)).Ok);
        c.Feeds.Save(id, "RSS", null, [c.Feed("one")]);
        Assert.True((await c.Sync(id)).Ok);
        c.Handler.Guid = "new-guid";
        Assert.True((await c.Sync(id)).Ok);
        Assert.True(c.Feeds.List(id).Single().PotentiallySaturated);
        c.Handler.Empty = true;
        Assert.True((await c.Sync(id)).Ok);
        using var db = c.Db.Open();
        Assert.Equal(2, db.ExecuteScalar<int>("SELECT COUNT(*) FROM releases"));
    }

    [Fact]
    public async Task CoexistingTorznabConfigurationIsUntouchedAndSourcesAreIndependent()
    {
        using var c = new Context();
        var old = c.Sources.Create("Existing Jackett", "http://localhost:9117/api", "old-key", "header");
        var rss = c.Feeds.Save(null, "RSS", null, [c.Feed("one")]);
        var second = c.Feeds.Save(null, "Other RSS", null, [c.Feed("two")]);
        Assert.True((await c.Sync(rss)).Ok);
        Assert.True((await c.Sync(second)).Ok);
        var source = c.Sources.Get(old)!;
        Assert.Equal("torznab", source.SourceKind);
        Assert.Equal("http://localhost:9117/api", source.TorznabUrl);
        Assert.Equal("old-key", source.ApiKey);
        Assert.Equal("header", source.AuthMode);
        using var db = c.Db.Open();
        Assert.Equal(2, db.ExecuteScalar<int>("SELECT COUNT(*) FROM releases"));
        Assert.Equal(3, c.Sources.ListEnabledForSync().Count);
    }

    [Theory]
    [InlineData("films")]
    [InlineData("series")]
    [InlineData("animation")]
    [InlineData("anime")]
    [InlineData("games")]
    [InlineData("comics")]
    [InlineData("books")]
    [InlineData("audio")]
    [InlineData("spectacle")]
    [InlineData("emissions")]
    public async Task EveryFeedarrCategorySurvivesTheExistingPipeline(string category)
    {
        using var c = new Context();
        var id = c.Feeds.Save(null, "Category", null, [c.Feed("one", category)]);
        Assert.True((await c.Sync(id)).Ok);
        using var db = c.Db.Open();
        Assert.Equal(category, db.ExecuteScalar<string>("SELECT top_category_key FROM releases"));
    }

    [Fact]
    public async Task RetentionAppliesAfterMergeAndDoesNotDeleteAbsentItems()
    {
        using var c = new Context();
        c.Handler.Count = 80;
        var id = c.Feeds.Save(null, "RSS", null, [c.Feed("one"), c.Feed("two")]);
        var result = await c.Sync(id);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(80, result.ItemsCount);
        Assert.Equal(80, result.InsertedNew);
        using var db = c.Db.Open();
        Assert.Equal(50, db.ExecuteScalar<int>("SELECT COUNT(*) FROM releases"));
        Assert.Equal(100, db.ExecuteScalar<int>("SELECT COUNT(*) FROM direct_rss_provenance"));
        c.Handler.Empty = true;
        Assert.True((await c.Sync(id)).Ok);
        Assert.Equal(50, db.ExecuteScalar<int>("SELECT COUNT(*) FROM releases"));
    }

    [Fact]
    public void AdditiveMigrationPreservesExistingSourceAndReleaseRows()
    {
        using var c = new Context();
        using var db = c.Db.Open();
        // Reconstruct the immediately preceding schema in this disposable DB only.
        db.Execute("DROP TABLE direct_rss_provenance; DROP TABLE direct_rss_feeds; ALTER TABLE sources DROP COLUMN source_kind; DELETE FROM schema_migrations WHERE id='0064_direct_rss.sql';");
        db.Execute("INSERT INTO sources(id,name,enabled,torznab_url,api_key,auth_mode,created_at_ts,updated_at_ts) VALUES(123,'Old',0,'http://localhost/api','encrypted-key','header',1,2)");
        db.Execute("INSERT INTO releases(source_id,guid,title,created_at_ts) VALUES(123,'old-guid','Old title',1)");
        new MigrationsRunner(c.Db, NullLogger<MigrationsRunner>.Instance).Run();
        Assert.Equal("torznab", db.ExecuteScalar<string>("SELECT source_kind FROM sources WHERE id=123"));
        Assert.Equal("encrypted-key", db.ExecuteScalar<string>("SELECT api_key FROM sources WHERE id=123"));
        Assert.Equal("header", db.ExecuteScalar<string>("SELECT auth_mode FROM sources WHERE id=123"));
        Assert.Equal(0, db.ExecuteScalar<int>("SELECT enabled FROM sources WHERE id=123"));
        Assert.Equal("old-guid", db.ExecuteScalar<string>("SELECT guid FROM releases"));
        Assert.Equal(0, db.ExecuteScalar<int>("SELECT COUNT(*) FROM direct_rss_feeds"));
    }

    [Fact]
    public async Task ManualAutomaticAndSchedulerUseTheSameRssPipeline()
    {
        using var c = new Context();
        var id = c.Feeds.Save(null, "RSS", null, [c.Feed("one")]);
        var source = c.Sources.Get(id)!;
        Assert.True((await c.Orchestration.ExecuteManualSyncAsync(source, false, default)).Ok);
        var auto = await c.Orchestration.ExecuteAutoSyncSourceAsync(source, default);
        Assert.True(auto.Ok, auto.Error);
        Assert.Equal(0, auto.InsertedNew);
        var scheduled = await c.Orchestration.ExecuteSchedulerSourceAsync(source, default);
        Assert.True(scheduled.Ok, scheduled.Error);
        Assert.Equal(0, scheduled.InsertedNew);
        Assert.Equal(3, c.Handler.Requests.Count);
    }

    [Fact]
    public async Task FourTr4kerFixturesUseTheRealIngestionAndRetentionPipelineOffline()
    {
        using var c = new Context();
        var names = new[] { ("films", "films"), ("animation", "animation"), ("general", "films"), ("spectacle", "spectacle") };
        var inputs = new List<DirectRssFeed>();
        var parsed = new List<DirectRssEntry>();
        foreach (var (name, category) in names)
        {
            var xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "DirectRss", $"tr4ker-{name}.xml"));
            c.Handler.Responses["/" + name] = xml;
            inputs.Add(c.Feed(name, category));
            parsed.AddRange(new DirectRssParser().Parse(xml, inputs.Count, category));
        }
        var expected = DirectRssMerger.Merge(parsed);
        var id = c.Feeds.Save(null, "TR4KER fixtures", null, inputs);
        var result = await c.Sync(id);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(expected.Entries.Count, result.ItemsCount);
        using var db = c.Db.Open();
        var total = db.ExecuteScalar<int>("SELECT COUNT(*) FROM releases");
        Assert.True(total > 0 && total <= 150);
        Assert.Equal(total, db.ExecuteScalar<int>("SELECT COUNT(DISTINCT guid) FROM releases"));
        Assert.Equal(4, c.Handler.Requests.Count);
        Assert.All(c.Feeds.List(id), f => Assert.Equal("ok", f.LastStatus));
    }

    [Fact]
    public async Task PortableConfigurationRoundTripSupportsRssWithoutChangingTorznabConfiguration()
    {
        using var c = new Context();
        var normalId = c.Sources.Create("Jackett", "http://example.org/api", "existing-key", "header");
        var disabled = c.Feed("two", "animation"); disabled.Enabled = false;
        var rssId = c.Feeds.Save(null, "RSS", "#123456", [c.Feed("one"), disabled]);
        Assert.True((await c.Sync(rssId)).Ok);
        var registry = new ExternalProviderRegistry();
        var archive = new ConfigurationArchiveService(c.Db, c.Protection, registry);
        const string password = "offline-test-passphrase";
        var bytes = await archive.ExportAsync(password);
        Assert.DoesNotContain("passkey=secret", Encoding.UTF8.GetString(bytes));
        var read = await archive.ReadAsync(new MemoryStream(bytes), password);
        Assert.Equal(2, read.Preview.Sources);
        Assert.Single(read.Configuration.Sources);
        Assert.Single(read.Configuration.RssSources!);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(read.Preview));
        c.Feeds.Save(rssId, "Changed", null, []);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var restore = new ConfigurationRestoreService(c.Db, archive, c.Protection, registry,
            new BackupExecutionCoordinator(), cache, new OutboundDestinationPolicy(new OutboundSecurityOptions()));
        var preview = await restore.PreviewAsync(new MemoryStream(bytes), password);
        await restore.ApplyAsync(new MemoryStream(bytes), password, preview.ExpectedState);
        var feeds = c.Feeds.List(rssId);
        Assert.Equal(2, feeds.Count);
        Assert.Contains(feeds, f => !f.Enabled && f.CategoryKey == "animation");
        Assert.All(feeds, f => Assert.Contains("passkey=secret", f.Url));
        Assert.Equal("rss", c.Sources.Get(rssId)!.SourceKind);
        Assert.Equal("existing-key", c.Sources.Get(normalId)!.ApiKey);
        Assert.Equal("header", c.Sources.Get(normalId)!.AuthMode);
        using var db = c.Db.Open();
        Assert.Equal(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM releases"));
        Assert.All(db.Query<string>("SELECT url_encrypted FROM direct_rss_feeds"), value => Assert.True(c.Protection.IsProtected(value)));

        using var other = new Context();
        var otherArchives = new ConfigurationArchiveService(other.Db, other.Protection, registry);
        var otherRestore = new ConfigurationRestoreService(other.Db, otherArchives, other.Protection, registry,
            new BackupExecutionCoordinator(), cache, new OutboundDestinationPolicy(new OutboundSecurityOptions()));
        var portable = await otherRestore.PreviewAsync(new MemoryStream(bytes), password);
        await otherRestore.ApplyAsync(new MemoryStream(bytes), password, portable.ExpectedState);
        var restoredRss = other.Sources.List().Single(s => s.SourceKind == "rss");
        Assert.Equal(2, other.Feeds.List(restoredRss.Id).Count);
        Assert.All(other.Feeds.List(restoredRss.Id), f => Assert.Contains("passkey=secret", f.Url));
    }

    [Fact]
    public async Task RssDownloadsUseStrictTransportAndNeverLogSignedExceptions()
    {
        using var c = new Context();
        var id = c.Feeds.Save(null, "RSS", null, [c.Feed("one")]);
        Assert.True((await c.Sync(id)).Ok);
        using var db = c.Db.Open();
        var releaseId = db.ExecuteScalar<long>("SELECT id FROM releases");
        var releases = new ReleaseRepository(c.Db, new TitleParser(), new UnifiedCategoryResolver());
        var factory = new DownloadFactory();
        var logger = new DownloadLogger();
        var policy = new OutboundDestinationPolicy(new OutboundSecurityOptions { TrustedNetworks = ["10.50.0.0/24"] });
        var controller = new ReleasesController(releases, c.Db, new(), null!, factory, logger, policy);
        var failed = Assert.IsType<ObjectResult>(await controller.Download(releaseId, default));
        Assert.Equal(502, failed.StatusCode);
        Assert.Equal("rss-torrent-download", factory.Name);
        Assert.DoesNotContain("secret", logger.Text);
        db.Execute("UPDATE releases SET download_url='http://10.50.0.1/torrent'");
        factory.Name = null;
        Assert.IsType<BadRequestObjectResult>(await controller.Download(releaseId, default));
        Assert.Null(factory.Name);
    }

    private sealed class DownloadFactory : IHttpClientFactory
    {
        public string? Name;
        public HttpClient CreateClient(string name) { Name = name; return new HttpClient(new DownloadFailure()); }
    }
    private sealed class DownloadFailure : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException(request.RequestUri!.AbsoluteUri);
    }
    private sealed class DownloadLogger : ILogger<ReleasesController>
    {
        public string Text = "";
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Text += formatter(state, exception) + exception?.ToString();
    }

    [Fact]
    public async Task HttpRoutesAcceptBrowserPayloadsAndValidateBeforeFetching()
    {
        using var c = new Context();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(c.Sources);
        builder.Services.AddSingleton(c.Feeds);
        builder.Services.AddSingleton(c.Client);
        builder.Services.AddControllers(options => options.Filters.Add<Feedarr.Api.Filters.ApiErrorNormalizationFilter>()).AddApplicationPart(typeof(DirectRssController).Assembly);
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var feed = new { id = 0, url = "https://example.org/one?passkey=synthetic", name = "", categoryKey = "films", enabled = true };
        var test = await http.PostAsJsonAsync("/api/sources/rss/test", feed);
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        Assert.True((await test.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
        using var db = c.Db.Open();
        Assert.Equal(0, db.ExecuteScalar<int>("SELECT COUNT(*) FROM releases"));
        Assert.Equal(0, db.ExecuteScalar<int>("SELECT COUNT(*) FROM sources"));
        var created = await http.PostAsJsonAsync("/api/sources/rss", new { name = "Browser RSS", color = "#123456", feeds = new[] { feed, feed with { url = "https://example.org/two", categoryKey = "animation" } } });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var saved = await http.GetFromJsonAsync<JsonElement>($"/api/sources/rss/{id}/feeds");
        Assert.Equal(2, saved.GetArrayLength());
        Assert.DoesNotContain("synthetic", saved.ToString());
        var firstId = saved[0].GetProperty("id").GetInt64();
        var edited = await http.PutAsJsonAsync($"/api/sources/rss/{id}", new { name = "Edited", feeds = new[] { new { id = firstId, url = "", categoryKey = "films", enabled = false } } });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal("Edited", c.Sources.Get(id)!.Name);
        Assert.False(c.Feeds.List(id).Single().Enabled);
        Assert.Contains("synthetic", c.Feeds.List(id).Single().Url);
        var requestsBefore = c.Handler.Requests.Count;
        var invalid = await http.PostAsJsonAsync("/api/sources/rss/test", feed with { url = "not-an-url" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var invalidBody = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("RSS URL or Feedarr category invalid", invalidBody.GetProperty("title").GetString());
        var invalidSave = await http.PostAsJsonAsync("/api/sources/rss", new { name = "Invalid URL", feeds = new[] { feed with { url = "file:///tmp/test.xml" } } });
        Assert.Equal(HttpStatusCode.BadRequest, invalidSave.StatusCode);
        var missing = await http.PostAsJsonAsync("/api/sources/rss", new { name = "Missing category", feeds = new[] { feed with { categoryKey = "" } } });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var unknown = await http.PutAsJsonAsync($"/api/sources/rss/{id}", new { name = "Unknown feed", feeds = new[] { new { id = long.MaxValue, categoryKey = "films", url = feed.url } } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(requestsBefore, c.Handler.Requests.Count);
        Assert.Equal(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM sources"));
        await app.StopAsync();
    }

    private sealed class Context : IDisposable
    {
        private readonly StatsTestWorkspace workspace = new();
        public Db Db { get; }
        public SourceRepository Sources { get; }
        public DirectRssRepository Feeds { get; }
        public DirectRssClient Client { get; }
        public IApiKeyProtectionService Protection { get; }
        public FeedHandler Handler { get; } = new();
        private readonly SyncExecutor executor;
        public SyncOrchestrationService Orchestration { get; }
        public Context()
        {
            Db = SystemStatsTestFactory.CreateDb(workspace);
            new MigrationsRunner(Db, NullLogger<MigrationsRunner>.Instance).Run();
            var protection = new ApiKeyProtectionService(new EphemeralDataProtectionProvider(), NullLogger<ApiKeyProtectionService>.Instance);
            Protection = protection;
            Sources = new(Db, protection);
            Feeds = new(Db, protection);
            Client = new(new HttpClient(Handler), new());
            var activity = new ActivityRepository(Db, new BadgeSignal());
            var releases = new ReleaseRepository(Db, new TitleParser(), new UnifiedCategoryResolver());
            var retention = new RetentionService(releases, null, new PosterFileStore(), NullLogger<RetentionService>.Instance);
            var stats = new ProviderStatsService(new StatsRepository(Db, new MemoryCache(new MemoryCacheOptions())));
            executor = new(new TorznabClient(new HttpClient(new RejectHandler()), new(), NullLogger<TorznabClient>.Instance),
                Sources, releases, activity, new Queue(), new PosterFetchJobFactory(releases), new(), retention, stats,
                NullLogger<SyncExecutor>.Instance, new DirectRssSyncService(Client, Feeds, activity));
            Orchestration = new SyncOrchestrationService(null!, Sources, releases, activity, new SettingsRepository(Db),
                new Queue(), new PosterFetchJobFactory(releases), new(), retention, stats,
                OptionsFactory.Create(new AppOptions { RssLimit = 50, RssLimitPerCategory = 50, RssLimitGlobalPerSource = 250 }),
                new TestHostApplicationLifetime(), NullLogger<SyncOrchestrationService>.Instance, executor: executor);
        }
        public DirectRssFeed Feed(string name, string category = "films") => new() { Name = name, Url = $"https://example.org/{name}?passkey=secret", CategoryKey = category };
        public Task<SyncExecutionResult> Sync(long id)
        {
            var input = new SyncPlanInput(Sources.Get(id)!, new(50,250,0,false,true,true), new(), [], [], [], [], 0, "test", "manual");
            return executor.ExecuteAsync(new SyncPlanBuilder().Build(input, new ManualSyncPolicy()), default);
        }
        public void Dispose() => workspace.Dispose();
    }
    private sealed class FeedHandler : HttpMessageHandler
    {
        public string Guid = "stable-guid";
        public bool Empty;
        public int Count = 1;
        public List<string> Requests { get; } = [];
        public Dictionary<string, string> Responses { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.AbsolutePath == "/fail") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (Responses.TryGetValue(request.RequestUri.AbsolutePath, out var xml))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml) });
            var item = Empty ? "" : string.Concat(Enumerable.Range(0, Count).Select(i =>
                $"<item><guid>{Guid}{(Count > 1 ? i.ToString() : "")}</guid><title>Movie {i} 2024 1080p</title><enclosure url='https://example.org/download?passkey=secret' length='123'/></item>"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"<rss version='2.0'><channel>{item}</channel></rss>") });
        }
    }
    private sealed class RejectHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("Torznab must not be called for RSS");
    }
    private sealed class Queue : IPosterFetchQueue
    {
        public int Jobs;
        public ValueTask<PosterFetchEnqueueResult> EnqueueAsync(PosterFetchJob job, CancellationToken ct, TimeSpan timeout)
            => ValueTask.FromResult(new PosterFetchEnqueueResult(PosterFetchEnqueueStatus.Enqueued));
        public ValueTask<PosterFetchEnqueueBatchResult> EnqueueManyAsync(IReadOnlyList<PosterFetchJob> jobs, CancellationToken ct, TimeSpan timeout)
            { Interlocked.Add(ref Jobs, jobs.Count); return ValueTask.FromResult(new PosterFetchEnqueueBatchResult(jobs.Count, 0, 0, 0)); }

        public ValueTask<PosterFetchJob> DequeueAsync(CancellationToken ct)
            => ValueTask.FromCanceled<PosterFetchJob>(ct);

        public void RecordRetry() { }

        public PosterFetchJob? Complete(PosterFetchJob job, PosterFetchProcessResult result) => null;

        public int ClearPending() => 0;

        public int Count => 0;

        public PosterFetchQueueSnapshot GetSnapshot()
            => new(0, 0, false, null, null, null, null, 0, 0, 0, 0, 0, 0, 0);
    }

}
