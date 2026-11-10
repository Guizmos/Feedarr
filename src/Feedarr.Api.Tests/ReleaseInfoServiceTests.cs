using System.Net;
using Feedarr.Api.Options;
using Feedarr.Api.Controllers;
using Feedarr.Api.Dtos.Updates;
using Microsoft.AspNetCore.Mvc;
using Feedarr.Api.Services.Updates;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Tests;

[CollectionDefinition("Release version environment", DisableParallelization = true)]
public sealed class ReleaseVersionEnvironmentCollection { }

[Collection("Release version environment")]
public sealed class ReleaseInfoServiceTests
{
    private const string Releases = """
        [{"tag_name":"v2.5.17","name":"Release","body":"Changelog","published_at":"2026-10-27T18:12:38Z","html_url":"https://github.com/Guizmos/Feedarr/releases/tag/v2.5.17","prerelease":false}]
        """;

    [Fact]
    public async Task BetaWithoutPrereleases_KeepsStableMetadataAndChangelog_WithoutOfferingStableUpdate()
    {
        var previous = Environment.GetEnvironmentVariable("FEEDARR_VERSION");
        try
        {
            Environment.SetEnvironmentVariable("FEEDARR_VERSION", "2.5.18-beta-sha.1234567");
            var service = Create((_, _) => Task.FromResult(Response(HttpStatusCode.OK, Releases)));
            var result = await service.GetLatestAsync(false, default);
            Assert.Null(result.Error);
            Assert.Equal("v2.5.17", result.LatestRelease?.TagName);
            Assert.Equal("Changelog", Assert.Single(result.Releases).Body);
            Assert.NotNull(result.LatestRelease?.PublishedAt);
            Assert.False(result.IsUpdateAvailable);
        }
        finally { Environment.SetEnvironmentVariable("FEEDARR_VERSION", previous); }
    }

    [Theory]
    [InlineData("2.5.16", "v2.5.17", true)]
    [InlineData("2.5.18-beta.1", "v2.5.18-beta.2", true)]
    public async Task ExistingTrackSelectionAndUpdateComparison_ArePreserved(string current, string expected, bool available)
    {
        var previous = Environment.GetEnvironmentVariable("FEEDARR_VERSION");
        try
        {
            Environment.SetEnvironmentVariable("FEEDARR_VERSION", current);
            var json = Releases.Replace("]", ", {\"tag_name\":\"v2.5.18-beta.2\",\"body\":\"Beta changelog\",\"prerelease\":true}]");
            var result = await Create((_, _) => Task.FromResult(Response(HttpStatusCode.OK, json)))
                .GetLatestAsync(false, default);
            Assert.Equal(expected, result.LatestRelease?.TagName);
            Assert.Equal(available, result.IsUpdateAvailable);
        }
        finally { Environment.SetEnvironmentVariable("FEEDARR_VERSION", previous); }
    }

    [Theory]
    [InlineData("[]", false)]
    [InlineData("null", true)]
    [InlineData("", true)]
    [InlineData("{}", true)]
    public async Task EmptyList_IsDistinctFromInvalidResponse(string json, bool error)
    {
        var result = await Create((_, _) => Task.FromResult(Response(HttpStatusCode.OK, json)))
            .GetLatestAsync(false, default);
        Assert.Empty(result.Releases);
        Assert.Equal(error, result.Error is not null);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task HttpFailures_AreNotAnEmptySuccessfulCheck(int status)
    {
        var service = Create((_, _) => Task.FromResult(Response((HttpStatusCode)status, "[]")));
        var response = Assert.IsType<OkObjectResult>(await new UpdatesController(service).Latest());
        var dto = Assert.IsType<UpdateCheckDto>(response.Value);
        Assert.NotNull(dto.Error);
        Assert.Null(dto.LatestRelease);
    }

    [Fact]
    public async Task NetworkFailure_IsReported()
    {
        var result = await Create((_, _) => throw new HttpRequestException("DNS unavailable"))
            .GetLatestAsync(false, default);
        Assert.Contains("connexion", result.Error);
    }

    [Fact]
    public async Task Timeout_IsReported()
    {
        var result = await Create((_, _) => throw new TaskCanceledException("timeout"))
            .GetLatestAsync(false, default);
        Assert.Contains("Délai", result.Error);
    }

    [Fact]
    public async Task FailedRefresh_PreservesCachedChangelogAndReportsFailure()
    {
        var calls = 0;
        var service = Create((_, _) => ++calls == 1
            ? Task.FromResult(Response(HttpStatusCode.OK, Releases))
            : throw new HttpRequestException("offline"));
        await service.GetLatestAsync(false, default);
        var result = await service.GetLatestAsync(true, default);
        Assert.NotNull(result.Error);
        Assert.Equal("Changelog", Assert.Single(result.Releases).Body);
        var cachedFailure = await service.GetLatestAsync(false, default);
        Assert.Equal(result.Error, cachedFailure.Error);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CallerCancellation_IsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct)).GetLatestAsync(false, cts.Token));
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json) };
    private static ReleaseInfoService Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new MemoryCache(new MemoryCacheOptions()), new Factory(send), new Monitor(), NullLogger<ReleaseInfoService>.Instance);
    private sealed class Factory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(send));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private sealed class Monitor : IOptionsMonitor<UpdatesOptions>
    {
        public UpdatesOptions CurrentValue { get; } = new();
        public UpdatesOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<UpdatesOptions, string?> listener) => null;
    }
}
