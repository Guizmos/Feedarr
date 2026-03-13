using System.Net;
using System.Text;
using System.Text.Json;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Dtos.Providers;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Metadata;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Tmdb;
using Feedarr.Api.Services.Titles;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class SchedulerControllerBackfillAllTests
{
    [Fact]
    public async Task BackfillMediaEntities_ModeAll_IncludesMetadataPass()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
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
        var resolver = new ActiveExternalProviderConfigResolver(
            instances,
            registry,
            NullLogger<ActiveExternalProviderConfigResolver>.Instance);
        var tmdb = new TmdbClient(new HttpClient(new NoOpTmdbHandler()), settings, stats, resolver);

        var releases = new ReleaseRepository(db, new TitleParser(), new UnifiedCategoryResolver());
        var externalIds = new ExternalIdBackfillService(releases, tmdb, NullLogger<ExternalIdBackfillService>.Instance);
        var requestResolver = new RequestTmdbResolverService(releases, tmdb, NullLogger<RequestTmdbResolverService>.Instance);
        var requestBackfill = new RequestTmdbBackfillService(releases, requestResolver);
        var metadataBackfill = new TmdbMetadataBackfillService(releases, tmdb, NullLogger<TmdbMetadataBackfillService>.Instance);

        var controller = new SchedulerController(
            sources: null!,
            releases: releases,
            activity: null!,
            syncOrchestration: null!,
            entityStatus: null!,
            externalIdBackfill: externalIds,
            requestTmdbBackfill: requestBackfill,
            tmdbMetadataBackfill: metadataBackfill,
            backupCoordinator: new BackupExecutionCoordinator(),
            log: NullLogger<SchedulerController>.Instance);

        var action = await controller.BackfillMediaEntities(limit: 100, mode: "all", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(action);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        Assert.Equal("all", doc.RootElement.GetProperty("mode").GetString());
        Assert.True(doc.RootElement.TryGetProperty("metadataScanned", out _));
        Assert.True(doc.RootElement.TryGetProperty("metadataEligible", out _));
        Assert.True(doc.RootElement.TryGetProperty("metadataProcessed", out _));
        Assert.True(doc.RootElement.TryGetProperty("metadataLocalPropagated", out _));
        Assert.True(doc.RootElement.TryGetProperty("metadataTmdbRefreshed", out _));
        Assert.True(doc.RootElement.TryGetProperty("metadataUniqueTmdbKeysRefreshed", out _));
        Assert.True(doc.RootElement.TryGetProperty("metadataSkipped", out _));
        Assert.True(doc.RootElement.TryGetProperty("metadataErrors", out _));
    }

    private static Db CreateDb(TestWorkspace workspace)
    {
        var options = OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        });
        return new Db(options);
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

    private sealed class PassthroughProtectionService : IApiKeyProtectionService
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
        public bool TryUnprotect(string protectedValue, out string? plaintext)
        {
            plaintext = protectedValue;
            return true;
        }
        public bool IsProtected(string value) => false;
    }

    private sealed class NoOpTmdbHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"results\":[]}", Encoding.UTF8, "application/json")
            });
    }
}
