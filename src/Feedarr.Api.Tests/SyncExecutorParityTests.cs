using System.Net;
using System.Text;
using System.Text.Json;
using System.IO;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Sync;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Torznab;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class SyncExecutorParityTests
{
    [Fact]
    public async Task ManualAndAutoPolicies_ProduceSameUpsertedItemsForSameDataset()
    {
        using var context = new SyncTestContext();
        var manualSource = context.CreateConfiguredSource("Manual", "http://localhost:9117/api/manual");
        var autoSource = context.CreateConfiguredSource("Auto", "http://localhost:9117/api/auto");

        var manual = await context.Service.ExecuteSourceSyncAsync(manualSource, new ManualSyncPolicy(), rssOnly: false, CancellationToken.None);
        var auto = await context.Service.ExecuteSourceSyncAsync(autoSource, new AutoSyncPolicy(), rssOnly: false, CancellationToken.None);

        Assert.True(manual.Ok);
        Assert.True(auto.Ok);
        Assert.Equal(manual.ItemsCount, auto.ItemsCount);
        Assert.Equal(manual.InsertedNew, auto.InsertedNew);
        Assert.Equal(manual.SyncMode, auto.SyncMode);
        Assert.Equal(manual.UsedMode, auto.UsedMode);
    }

    [Fact]
    public async Task SchedulerPolicy_IsAlignedWithAutoForSameDataset()
    {
        using var context = new SyncTestContext();
        var autoSource = context.CreateConfiguredSource("Auto", "http://localhost:9117/api/auto");
        var schedulerSource = context.CreateConfiguredSource("Scheduler", "http://localhost:9117/api/scheduler");

        var auto = await context.Service.ExecuteSourceSyncAsync(autoSource, new AutoSyncPolicy(), rssOnly: false, CancellationToken.None);
        var scheduler = await context.Service.ExecuteSourceSyncAsync(schedulerSource, new SchedulerSyncPolicy(), rssOnly: false, CancellationToken.None);

        Assert.True(auto.Ok);
        Assert.True(scheduler.Ok);
        Assert.Equal(auto.ItemsCount, scheduler.ItemsCount);
        Assert.Equal(auto.InsertedNew, scheduler.InsertedNew);
        Assert.Equal(auto.SyncMode, scheduler.SyncMode);
        Assert.Equal(auto.UsedMode, scheduler.UsedMode);
    }

    [Fact]
    public async Task ManualAndAutoSyncActivity_AlwaysIncludeCategoryIdsInDataJson()
    {
        using var context = new SyncTestContext();
        var manualSource = context.CreateConfiguredSource("Manual", "http://localhost:9117/api/manual");
        var autoSource = context.CreateConfiguredSource("Auto", "http://localhost:9117/api/auto");

        var manual = await context.Service.ExecuteSourceSyncAsync(manualSource, new ManualSyncPolicy(), rssOnly: false, CancellationToken.None);
        var auto = await context.Service.ExecuteSourceSyncAsync(autoSource, new AutoSyncPolicy(), rssOnly: false, CancellationToken.None);

        Assert.True(manual.Ok);
        Assert.True(auto.Ok);

        using var manualData = GetLatestSyncSuccessDataJson(context.Activity, manualSource.Id);
        using var autoData = GetLatestSyncSuccessDataJson(context.Activity, autoSource.Id);

        Assert.True(manualData.RootElement.TryGetProperty("categoryIds", out var manualCategoryIds));
        Assert.Contains(manualCategoryIds.EnumerateArray().Select(x => x.GetInt32()), id => id == 2000);
        Assert.True(manualData.RootElement.TryGetProperty("categories", out var manualCategories));
        Assert.Contains(
            manualCategories.EnumerateArray()
                .Select(x => x.TryGetProperty("id", out var id) ? id.GetInt32() : -1),
            id => id == 2000);

        Assert.True(autoData.RootElement.TryGetProperty("categoryIds", out var autoCategoryIds));
        Assert.Contains(autoCategoryIds.EnumerateArray().Select(x => x.GetInt32()), id => id == 2000);
        Assert.True(autoData.RootElement.TryGetProperty("categories", out var autoCategories));
        Assert.Contains(
            autoCategories.EnumerateArray()
                .Select(x => x.TryGetProperty("id", out var id) ? id.GetInt32() : -1),
            id => id == 2000);
    }

    [Fact]
    public async Task ManualAndAutoSyncActivity_ListEndpointAlwaysContainsCanonicalCategories()
    {
        using var context = new SyncTestContext();
        var manualSource = context.CreateConfiguredSource("Manual", "http://localhost:9117/api/manual");
        var autoSource = context.CreateConfiguredSource("Auto", "http://localhost:9117/api/auto");

        var manual = await context.Service.ExecuteSourceSyncAsync(manualSource, new ManualSyncPolicy(), rssOnly: false, CancellationToken.None);
        var auto = await context.Service.ExecuteSourceSyncAsync(autoSource, new AutoSyncPolicy(), rssOnly: false, CancellationToken.None);

        Assert.True(manual.Ok);
        Assert.True(auto.Ok);

        var manualEntry = GetLatestSyncSuccessEntry(context.Activity, manualSource.Id);
        var autoEntry = GetLatestSyncSuccessEntry(context.Activity, autoSource.Id);

        Assert.True(manualEntry.TryGetValue("categories", out var manualCategoriesRaw));
        Assert.NotNull(manualCategoriesRaw);
        var manualCategories = ((IEnumerable<object>)manualCategoriesRaw!)
            .Select(ToCategorySnapshot)
            .Where(snapshot => snapshot is not null)
            .Select(snapshot => snapshot!)
            .ToList();
        Assert.Contains(manualCategories, category => category.Id == 2000);
        Assert.Contains(manualCategories, category => !string.IsNullOrWhiteSpace(category.Label));

        Assert.True(autoEntry.TryGetValue("categories", out var autoCategoriesRaw));
        Assert.NotNull(autoCategoriesRaw);
        var autoCategories = ((IEnumerable<object>)autoCategoriesRaw!)
            .Select(ToCategorySnapshot)
            .Where(snapshot => snapshot is not null)
            .Select(snapshot => snapshot!)
            .ToList();
        Assert.Contains(autoCategories, category => category.Id == 2000);
        Assert.Contains(autoCategories, category => !string.IsNullOrWhiteSpace(category.Label));
    }

    private static JsonDocument GetLatestSyncSuccessDataJson(ActivityRepository activity, long sourceId)
    {
        var row = GetLatestSyncSuccessEntry(activity, sourceId);
        if (!row.TryGetValue("dataJson", out var rawDataJson) || rawDataJson is not string dataJson || string.IsNullOrWhiteSpace(dataJson))
            throw new Xunit.Sdk.XunitException($"No sync success activity dataJson found for sourceId={sourceId}.");

        return JsonDocument.Parse(dataJson);
    }

    private static IDictionary<string, object> GetLatestSyncSuccessEntry(ActivityRepository activity, long sourceId)
    {
        foreach (var entry in activity.List(limit: 50, sourceId: sourceId, eventType: "sync", level: "info"))
        {
            if (entry is not IDictionary<string, object> row)
                continue;

            if (!row.TryGetValue("dataJson", out var rawDataJson))
                continue;

            var dataJson = rawDataJson as string;
            if (string.IsNullOrWhiteSpace(dataJson))
                continue;

            JsonDocument parsed;
            try
            {
                parsed = JsonDocument.Parse(dataJson);
            }
            catch
            {
                continue;
            }

            if (parsed.RootElement.TryGetProperty("itemsCount", out _))
                return row;

            parsed.Dispose();
        }

        throw new Xunit.Sdk.XunitException($"No sync success activity entry found for sourceId={sourceId}.");
    }

    private static CategorySnapshot? ToCategorySnapshot(object? raw)
    {
        if (raw is IDictionary<string, object> data)
        {
            if (!data.TryGetValue("id", out var idRaw) || idRaw is null)
                return null;
            var idFromDict = Convert.ToInt32(idRaw);
            var labelFromDict = data.TryGetValue("label", out var labelRaw) ? labelRaw?.ToString() : null;
            return new CategorySnapshot(idFromDict, labelFromDict);
        }

        if (raw is null)
            return null;

        var type = raw.GetType();
        var idProp = type.GetProperty("Id") ?? type.GetProperty("id");
        if (idProp is null)
            return null;
        var idValue = idProp.GetValue(raw);
        if (idValue is null)
            return null;
        var id = Convert.ToInt32(idValue);

        var labelProp = type.GetProperty("Label") ?? type.GetProperty("label");
        var label = labelProp?.GetValue(raw)?.ToString();

        return new CategorySnapshot(id, label);
    }

    private sealed record CategorySnapshot(int Id, string? Label);

    private sealed class SyncTestContext : IDisposable
    {
        private readonly TestWorkspace _workspace = new();

        public SyncTestContext()
        {
            Options = OptionsFactory.Create(new AppOptions
            {
                DataDir = _workspace.DataDir,
                DbFileName = "feedarr.db",
                RssLimitPerCategory = 50,
                RssLimitGlobalPerSource = 250
            });

            Db = new Db(Options);
            new MigrationsRunner(Db, NullLogger<MigrationsRunner>.Instance).Run();

            var protection = new PassthroughProtectionService();
            Settings = new SettingsRepository(Db, protection, NullLogger<SettingsRepository>.Instance);
            Settings.SaveGeneral(new GeneralSettings
            {
                SyncIntervalMinutes = 60,
                RssLimitPerCategory = 50,
                RssLimitGlobalPerSource = 250,
                RssLimit = 50,
                AutoSyncEnabled = true
            });
            Settings.SaveUi(new UiSettings
            {
                HideSeenByDefault = true
            });

            Sources = new SourceRepository(Db, protection);
            Releases = new ReleaseRepository(Db, new TitleParser(), new UnifiedCategoryResolver());
            Activity = new ActivityRepository(Db, new BadgeSignal());
            ProviderStats = new ProviderStatsService(new StatsRepository(Db, new MemoryCache(new MemoryCacheOptions())));
            Retention = new RetentionService(Releases, null!, new PosterFileStore(), NullLogger<RetentionService>.Instance);
            Queue = new AlwaysEnqueuePosterFetchQueue();

            var torznab = new TorznabClient(
                new HttpClient(new StaticTorznabFeedHandler()),
                new TorznabRssParser(),
                NullLogger<TorznabClient>.Instance);

            Service = new SyncOrchestrationService(
                torznab,
                Sources,
                Releases,
                Activity,
                Settings,
                Queue,
                new PosterFetchJobFactory(Releases),
                new UnifiedCategoryResolver(),
                Retention,
                ProviderStats,
                Options,
                new TestHostApplicationLifetime(),
                NullLogger<SyncOrchestrationService>.Instance);
        }

        public Db Db { get; }
        public SettingsRepository Settings { get; }
        public SourceRepository Sources { get; }
        public ReleaseRepository Releases { get; }
        public ActivityRepository Activity { get; }
        public ProviderStatsService ProviderStats { get; }
        public RetentionService Retention { get; }
        public AlwaysEnqueuePosterFetchQueue Queue { get; }
        public SyncOrchestrationService Service { get; }
        public Microsoft.Extensions.Options.IOptions<AppOptions> Options { get; }

        public Feedarr.Api.Models.Source CreateConfiguredSource(string name, string url)
        {
            var sourceId = Sources.Create(name, url, "secret", "query");
            Sources.ReplaceSelectedCategoryIds(sourceId, [2000]);
            Sources.PatchCategoryMappings(sourceId, [
                new SourceRepository.SourceCategoryMappingPatch
                {
                    CatId = 2000,
                    GroupKey = "movie"
                }
            ]);

            return Sources.Get(sourceId)!;
        }

        public void Dispose()
        {
            _workspace.Dispose();
        }
    }

    private sealed class StaticTorznabFeedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BuildRssFeed(), Encoding.UTF8, "application/xml")
            });
        }

        private static string BuildRssFeed()
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<rss version=\"2.0\" xmlns:torznab=\"http://torznab.com/schemas/2015/feed\">");
            sb.AppendLine("  <channel>");
            sb.AppendLine("    <item>");
            sb.AppendLine("      <title>Movie A 2024</title>");
            sb.AppendLine("      <guid>guid-2000-a</guid>");
            sb.AppendLine("      <link>http://localhost/item/a</link>");
            sb.AppendLine("      <pubDate>Mon, 01 Jan 2024 00:00:00 GMT</pubDate>");
            sb.AppendLine("      <torznab:attr name=\"category\" value=\"2000\"/>");
            sb.AppendLine("    </item>");
            sb.AppendLine("    <item>");
            sb.AppendLine("      <title>Movie B 2023</title>");
            sb.AppendLine("      <guid>guid-2000-b</guid>");
            sb.AppendLine("      <link>http://localhost/item/b</link>");
            sb.AppendLine("      <pubDate>Sun, 31 Dec 2023 00:00:00 GMT</pubDate>");
            sb.AppendLine("      <torznab:attr name=\"category\" value=\"2000\"/>");
            sb.AppendLine("    </item>");
            sb.AppendLine("  </channel>");
            sb.AppendLine("</rss>");
            return sb.ToString();
        }
    }

    private sealed class AlwaysEnqueuePosterFetchQueue : IPosterFetchQueue
    {
        public ValueTask<PosterFetchEnqueueResult> EnqueueAsync(PosterFetchJob job, CancellationToken ct, TimeSpan timeout)
            => ValueTask.FromResult(new PosterFetchEnqueueResult(PosterFetchEnqueueStatus.Enqueued));
        public ValueTask<PosterFetchEnqueueBatchResult> EnqueueManyAsync(IReadOnlyList<PosterFetchJob> jobs, CancellationToken ct, TimeSpan timeout)
            => ValueTask.FromResult(new PosterFetchEnqueueBatchResult(jobs.Count, 0, 0, 0));

        public ValueTask<PosterFetchJob> DequeueAsync(CancellationToken ct)
            => ValueTask.FromCanceled<PosterFetchJob>(ct);

        public void RecordRetry() { }

        public PosterFetchJob? Complete(PosterFetchJob job, PosterFetchProcessResult result) => null;

        public int ClearPending() => 0;

        public int Count => 0;

        public PosterFetchQueueSnapshot GetSnapshot()
            => new(0, 0, false, null, null, null, null, 0, 0, 0, 0, 0, 0, 0);
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
            Root = Path.Combine(Path.GetTempPath(), "feedarr-sync-tests", Guid.NewGuid().ToString("N"));
            DataDir = Path.Combine(Root, "data");
            Directory.CreateDirectory(DataDir);
        }

        public string Root { get; }
        public string DataDir { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // best effort cleanup
            }
        }
    }
}
