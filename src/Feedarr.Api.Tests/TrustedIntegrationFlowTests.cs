using System.Net;
using System.Text;
using System.Text.Json;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Dtos.Arr;
using Feedarr.Api.Dtos.Categories;
using Feedarr.Api.Dtos.Providers;
using Feedarr.Api.Dtos.Sources;
using Feedarr.Api.Filters;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Arr;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Titles;
using Microsoft.Extensions.Hosting;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Jackett;
using Feedarr.Api.Services.Prowlarr;
using Feedarr.Api.Services.Resilience;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Torznab;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class TrustedIntegrationFlowTests
{
    private static OutboundDestinationPolicy Policy() => new(new OutboundSecurityOptions
    {
        TrustedHosts = new() { ["Prowlarr"] = "prowlarr:9696", ["Sonarr"] = "10.50.0.15:8989", ["Radarr"] = "10.50.0.16:7878" }
    });

    [Fact]
    public async Task SetupDiscoveryCapsRssAndSavedProviderShareDockerAuthorization()
    {
        using var context = new FlowContext(Policy());
        Assert.IsType<OkObjectResult>(context.ProviderController.Create(new()
        { Type = "prowlarr", BaseUrl = "prowlarr:9696/api/v1", ApiKey = "key" }));
        var provider = Assert.Single(context.Providers.List());
        var id = Convert.ToInt64(provider.Id);
        var discovery = await context.ProviderController.ListIndexers(id, default);
        var list = Assert.IsType<OkObjectResult>(discovery);
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(list.Value));
        var url = payload.RootElement[0].GetProperty("torznabUrl").GetString()!;
        Assert.Equal("http://prowlarr:9696/1/api", url);

        Assert.IsType<OkObjectResult>(await context.ProviderController.Test(id, default));
        Assert.IsType<OkObjectResult>(await context.SourcesController.Create(new SourceCreateDto
        { Name = "Docker", TorznabUrl = url, ApiKey = "key", AuthMode = "query" }, default));
        var source = Assert.Single(context.Sources.List());
        var sourceId = Convert.ToInt64(source.Id);
        var caps = await context.Categories.CapsProvider(new ProviderCapsRequestDto
        { ProviderId = id, IndexerId = "1" }, default);
        Assert.IsType<OkObjectResult>(caps.Result);
        var savedCaps = await context.Categories.CapsLegacy(new CapsCategoriesRequestDto { SourceId = sourceId }, default);
        Assert.IsType<OkObjectResult>(savedCaps.Result);
        var test = await context.SourcesController.TestById(sourceId, null, default);
        Assert.IsType<OkObjectResult>(test.Result);
        var rss = await context.Torznab.FetchLatestAsync(url, "query", "key", 20, default);
        Assert.Single(rss.items);
        Assert.IsType<OkObjectResult>(await context.SourcesController.GetCategoryPreviewLive(sourceId, 2000, 5, default));
        Assert.IsType<OkObjectResult>(await context.SourcesController.GetCategoryPreviewLiveTemp(new CategoryPreviewLiveTempDto
        { ProviderId = id, IndexerId = "1", CatId = 2000 }, default));
        Assert.All(context.Sockets.Endpoints, endpoint => Assert.Equal(9696, endpoint.Port));
        Assert.Contains(context.Transport.Paths, path => path.Contains("t=caps"));
        Assert.Contains(context.Transport.Paths, path => path.Contains("t=search"));
    }

    [Theory]
    [InlineData("sonarr", "http://10.50.0.15:8989")]
    [InlineData("radarr", "http://10.50.0.16:7878")]
    public async Task ArrPrivateIpWorksForInlineAndSavedSettings(string type, string url)
    {
        using var context = new FlowContext(Policy());
        var controller = context.ArrController;
        var result = await controller.Test(new ArrAppTestRequestDto { BaseUrl = url, ApiKey = "key" }, type, default);
        var body = Assert.IsType<ArrAppTestResultDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(body.Ok);
        var created = Assert.IsType<OkObjectResult>(await controller.Create(new ArrAppCreateDto
        { Type = type, BaseUrl = url, ApiKey = "key" }, default));
        var id = Assert.IsType<ArrAppResponseDto>(created.Value).Id;
        var saved = await controller.TestById(id, default);
        Assert.True(Assert.IsType<ArrAppTestResultDto>(Assert.IsType<OkObjectResult>(saved.Result).Value).Ok);
    }

    [Theory]
    [InlineData("overseerr")]
    [InlineData("jellyseerr")]
    [InlineData("seer")]
    public async Task RequestAppsSupportCidr(string type)
    {
        var policy = new OutboundDestinationPolicy(new OutboundSecurityOptions { TrustedNetworks = ["10.50.0.0/24"] });
        using var context = new FlowContext(policy);
        var result = await context.ArrController.Test(
            new ArrAppTestRequestDto { BaseUrl = "http://10.50.0.20:5055", ApiKey = "key" }, type, default);
        Assert.True(Assert.IsType<ArrAppTestResultDto>(Assert.IsType<OkObjectResult>(result.Result).Value).Ok);
    }

    [Fact]
    public async Task EmptyConfigurationDeniesPrivateDestinationsWithoutNetworkTraffic()
    {
        using var context = new FlowContext(new OutboundDestinationPolicy(new OutboundSecurityOptions()));
        var rejected = context.ProviderController.Create(new()
        { Type = "prowlarr", BaseUrl = "http://10.50.0.15:9696", ApiKey = "key" });
        Assert.Equal(SsrfBlockedException.Code, Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(rejected).Value).Title);
        Assert.Empty(context.Providers.List());
        await Assert.ThrowsAsync<SsrfBlockedException>(() => context.Prowlarr.ListIndexersAsync("http://prowlarr:9696", "key", default));
        Assert.Empty(context.Sockets.Endpoints);
        Assert.Empty(context.Transport.Paths);
    }

    [Fact]
    public async Task WrongPortIsBlockedAndDefaultDownloadValidationRemainsStrict()
    {
        using var context = new FlowContext(Policy());
        await Assert.ThrowsAsync<SsrfBlockedException>(() => context.Prowlarr.ListIndexersAsync("http://prowlarr:9697", "key", default));
        Assert.False(OutboundUrlGuard.TryNormalizeDownloadUrl("http://10.50.0.15:8989/torrent", out _, out _));
        using var strict = context.Client(OutboundDestinationContext.Strict);
        await Assert.ThrowsAsync<SsrfBlockedException>(() => strict.GetAsync("http://prowlarr:9696/torrent"));
        Assert.Empty(context.Sockets.Endpoints);
    }

    [Fact]
    public async Task SecurityFailuresNeverBecomeJackettOrTorznabFallbacks()
    {
        var transport = new FailureHandler(security: true);
        var jackett = new JackettClient(new HttpClient(transport));
        await Assert.ThrowsAsync<SsrfBlockedException>(() => jackett.ListIndexersAsync("http://jackett.test:9117", "key", default));
        Assert.Equal(1, transport.Calls);
        var torznab = new TorznabClient(new HttpClient(transport), new TorznabRssParser(), NullLogger<TorznabClient>.Instance);
        await Assert.ThrowsAsync<SsrfBlockedException>(() => torznab.FetchLatestAsync("http://jackett.test:9117/api", "query", "key", 20, default));
        Assert.Equal(2, transport.Calls);
        await Assert.ThrowsAsync<SsrfBlockedException>(() => torznab.FetchLatestByCategoriesAsync(
            "http://jackett.test:9117/api", "query", "key", 20, [2000, 5000], default));
        Assert.Equal(3, transport.Calls);
    }

    [Fact]
    public async Task OrdinaryProviderForbiddenStillUsesFunctionalFallback()
    {
        var transport = new FailureHandler(security: false);
        var jackett = new JackettClient(new HttpClient(transport));
        await Assert.ThrowsAsync<HttpRequestException>(() => jackett.ListIndexersAsync("http://jackett.test:9117", "key", default));
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public async Task ApiResponseCarriesStableCodeForStaticAndWrappedTransportRefusals()
    {
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor(), new ModelStateDictionary());
        var filter = new ApiErrorNormalizationFilter();
        var exceptionContext = new ExceptionContext(actionContext, [])
        { Exception = new HttpRequestException("transport wrapper", new SsrfBlockedException()) };
        filter.OnException(exceptionContext);
        Assert.True(exceptionContext.ExceptionHandled);
        var exceptionResult = Assert.IsType<ObjectResult>(exceptionContext.Result);
        Assert.Equal(403, exceptionResult.StatusCode);
        Assert.Equal(SsrfBlockedException.Code, Assert.IsType<ProblemDetails>(exceptionResult.Value).Extensions["code"]);

        var resultContext = new ResultExecutingContext(actionContext, [],
            new BadRequestObjectResult(new { error = SsrfBlockedException.Code }), new object());
        await filter.OnResultExecutionAsync(resultContext, () => Task.FromResult(new ResultExecutedContext(
            actionContext, [], resultContext.Result, new object())));
        Assert.Equal(SsrfBlockedException.Code, Assert.IsType<ProblemDetails>(
            Assert.IsType<ObjectResult>(resultContext.Result).Value).Extensions["code"]);
        exceptionContext = new ExceptionContext(actionContext, []) { Exception = new HttpRequestException("upstream", null, HttpStatusCode.Forbidden) };
        filter.OnException(exceptionContext);
        Assert.False(exceptionContext.ExceptionHandled);
    }

    [Fact]
    public async Task ManualAndAutomaticRssUseTheSameTrustedPolicy()
    {
        using var context = new FlowContext(Policy());
        var manualId = context.Sources.Create("Manual", "http://prowlarr:9696/1/api", "key", "query");
        var autoId = context.Sources.Create("Auto", "http://prowlarr:9696/2/api", "key", "query");
        Assert.IsType<OkObjectResult>(await context.SourcesController.Sync(manualId, true, default));
        var auto = await context.Sync.ExecuteAutoSyncSourceAsync(context.Sources.Get(autoId)!, default);
        Assert.True(auto.Ok, auto.Error);
        Assert.Equal(2, context.Transport.Paths.Count);
    }

    [Fact]
    public async Task ManualAndAutomaticRssPreserveSecurityRefusalWithoutFallback()
    {
        using var context = new FlowContext(new OutboundDestinationPolicy(new OutboundSecurityOptions()));
        var id = context.Sources.Create("Blocked", "http://prowlarr:9696/1/api", "key", "query");
        await Assert.ThrowsAsync<SsrfBlockedException>(() => context.SourcesController.Sync(id, false, default));
        var auto = await context.Sync.ExecuteAutoSyncSourceAsync(context.Sources.Get(id)!, default);
        Assert.False(auto.Ok);
        Assert.Equal(SsrfBlockedException.Code, auto.Error);
        Assert.Empty(context.Transport.Paths);
        Assert.Empty(context.Sockets.Endpoints);
    }

    [Theory]
    [InlineData("jackett", "http://10.50.0.20:9117/api/v2.0", "http://10.50.0.20:9117")]
    [InlineData("prowlarr", "http://10.50.0.20:9696/api/v1", "http://10.50.0.20:9696")]
    public void CidrSetupAndSettingsPreserveProviderNormalization(string type, string url, string expected)
    {
        using var context = new FlowContext(new OutboundDestinationPolicy(new OutboundSecurityOptions
        { TrustedNetworks = ["10.50.0.0/24"] }));
        Assert.IsType<OkObjectResult>(context.ProviderController.Create(new() { Type = type, BaseUrl = url, ApiKey = "key" }));
        var provider = Assert.Single(context.Providers.List());
        Assert.Equal(expected, (string)provider.BaseUrl);
        Assert.IsType<OkObjectResult>(context.ProviderController.Update(Convert.ToInt64(provider.Id), new ProviderUpdateDto
        { BaseUrl = url, ApiKey = "key" }));
    }

    [Fact]
    public async Task CapsSecurityRefusalDoesNotBecomeStoredCategoryFallback()
    {
        using var context = new FlowContext(new OutboundDestinationPolicy(new OutboundSecurityOptions()));
        var id = context.Sources.Create("Blocked", "http://prowlarr:9696/1/api", "key", "query");
        context.Sources.ReplaceCategories(id, [(2000, "Stored Movies", false, null)]);
        await Assert.ThrowsAsync<SsrfBlockedException>(() => context.Categories.CapsLegacy(
            new CapsCategoriesRequestDto { SourceId = id }, default));
        Assert.Empty(context.Transport.Paths);
        Assert.Empty(context.Sockets.Endpoints);
    }

    [Theory]
    [InlineData("http://8.8.8.8/api?custom=1", true)]
    [InlineData("http://10.50.0.15:8989/api?custom=1", false)]
    [InlineData("http://169.254.169.254/api", false)]
    [InlineData("http://[::ffff:127.0.0.1]/api", false)]
    public void StrictRemainsDefaultForTorznabValidation(string url, bool allowed)
    {
        Assert.Equal(allowed, OutboundUrlGuard.TryNormalizeTorznabUrl(url, out var normalized, out _));
        if (allowed) Assert.Contains("custom=1", normalized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecurityRefusalDoesNotRetryOrTripCircuitBreaker(bool wrapped)
    {
        var transport = new FailureHandler(security: true, wrapped);
        var state = new ArrCircuitBreakerState();
        using var retry = new TransientHttpRetryHandler { InnerHandler = transport };
        using var circuit = new ArrCircuitBreakerHandler(state, OptionsFactory.Create(new HttpResilienceOptions
        { Arr = new ResilienceFamilyOptions { MinimumThroughput = 1 } }), NullLogger<ArrCircuitBreakerHandler>.Instance)
        { InnerHandler = retry };
        using var invoker = new HttpMessageInvoker(circuit);
        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "http://provider.test/api"), default));
        Assert.True(SsrfBlockedException.IsBlocked(error));
        Assert.Equal(1, transport.Calls);
        Assert.False(state.IsOpen);
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }

    private sealed class FlowContext : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "feedarr-ssrf-flow", Guid.NewGuid().ToString("N"));
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());
        private readonly List<HttpClient> _clients = [];
        private readonly OutboundDestinationPolicy _policy;
        public readonly RecordingSockets Sockets = new();
        public readonly IntegrationTransport Transport = new();
        public ProviderRepository Providers { get; }
        public SourceRepository Sources { get; }
        public ProvidersController ProviderController { get; }
        public SourcesController SourcesController { get; }
        public CategoriesController Categories { get; }
        public ArrAppsController ArrController { get; }
        public TorznabClient Torznab { get; }
        public SyncOrchestrationService Sync { get; }
        public ProwlarrClient Prowlarr { get; }

        public FlowContext(OutboundDestinationPolicy policy)
        {
            _policy = policy;
            Directory.CreateDirectory(_dir);
            var db = new Db(OptionsFactory.Create(new AppOptions { DataDir = _dir }));
            new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();
            var protection = new Protection();
            var settings = new SettingsRepository(db);
            Providers = new ProviderRepository(db, protection);
            Sources = new SourceRepository(db, protection);
            var activity = new ActivityRepository(db, new BadgeSignal());
            var jackett = new JackettClient(Client(), policy);
            Prowlarr = new ProwlarrClient(Client(), policy);
            Torznab = new TorznabClient(Client(), new TorznabRssParser(), NullLogger<TorznabClient>.Instance, policy);
            var caps = new CategoryRecommendationService(Sources, Torznab, _cache, NullLogger<CategoryRecommendationService>.Instance);
            ProviderController = new ProvidersController(Providers, Sources, jackett, Prowlarr, _cache, NullLogger<ProvidersController>.Instance, policy);
            var releases = new ReleaseRepository(db, new TitleParser(), new UnifiedCategoryResolver());
            Sync = new SyncOrchestrationService(Torznab, Sources, releases, activity, settings,
                new PosterFetchQueue(NullLogger<PosterFetchQueue>.Instance), new PosterFetchJobFactory(releases),
                new UnifiedCategoryResolver(), new RetentionService(releases, null, new PosterFileStore(), NullLogger<RetentionService>.Instance),
                new ProviderStatsService(new StatsRepository(db, _cache)), OptionsFactory.Create(new AppOptions()),
                new Lifetime(), NullLogger<SyncOrchestrationService>.Instance);
            SourcesController = new SourcesController(Torznab, Sources, Providers, releases, activity, caps,
                new BackupExecutionCoordinator(), Sync, NullLogger<SourcesController>.Instance, policy);
            Categories = new CategoriesController(db, caps, Providers, _cache, NullLogger<CategoriesController>.Instance);
            ArrController = new ArrAppsController(new ArrApplicationRepository(db, protection), new SonarrClient(Client(), policy),
                new RadarrClient(Client(), policy), new EerrRequestClient(Client(), policy), null!, activity, NullLogger<ArrAppsController>.Instance, policy);
        }
        public HttpClient Client(OutboundDestinationContext context = OutboundDestinationContext.TrustedIntegration)
        {
            var dns = new FixedDns();
            var connector = new ValidatedOutboundConnector(_policy, dns, Sockets);
            var http = new HttpClient(new SsrfGuardHandler(_policy, dns, context)
            { InnerHandler = new ConnectionTransport(connector, context, Transport) });
            _clients.Add(http);
            return http;
        }
        public void Dispose()
        {
            foreach (var client in _clients) client.Dispose();
            _cache.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_dir, true);
        }
    }

    private sealed class FixedDns : IOutboundDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Task.FromResult(
            new[] { IPAddress.TryParse(host, out var ip) ? ip : IPAddress.Parse("172.20.0.2") });
    }
    private sealed class RecordingSockets : IOutboundSocketConnector
    {
        public readonly List<IPEndPoint> Endpoints = [];
        public ValueTask<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken ct)
        { Endpoints.Add(endpoint); return ValueTask.FromResult<Stream>(new MemoryStream()); }
    }
    private sealed class ConnectionTransport(ValidatedOutboundConnector connector, OutboundDestinationContext context,
        IntegrationTransport response) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var connection = await connector.ConnectAsync(request.RequestUri!.IdnHost, request.RequestUri.Port, context, ct);
            return response.Respond(request);
        }
    }
    private sealed class IntegrationTransport
    {
        public readonly List<string> Paths = [];
        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var uri = request.RequestUri!;
            Paths.Add(uri.PathAndQuery);
            var body = uri.AbsolutePath.EndsWith("/api/v1/indexer") ? "[{\"id\":1,\"name\":\"Docker\",\"enable\":true,\"protocol\":\"torrent\"}]"
                : uri.Query.Contains("t=caps") ? "<caps><categories><category id=\"2000\" name=\"Movies\"/></categories></caps>"
                : uri.Query.Contains("t=") ? "<rss version=\"2.0\"><channel><item><title>Movie 2024</title><guid>movie-1</guid><category>2000</category></item></channel></rss>"
                : "{\"version\":\"1.0\",\"appName\":\"Test\"}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, body.StartsWith('<') ? "application/xml" : "application/json") };
        }
    }
    private sealed class FailureHandler(bool security, bool wrapped = false) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (security)
            {
                if (wrapped) throw new HttpRequestException("transport wrapper", new SsrfBlockedException());
                throw new SsrfBlockedException();
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        }
    }
    private sealed class Protection : IApiKeyProtectionService
    {
        public string Protect(string text) => text;
        public string Unprotect(string text) => text;
        public bool TryUnprotect(string text, out string plainText) { plainText = text; return true; }
        public bool IsProtected(string value) => false;
    }
}
