using Feedarr.Api.Services.Matching;
using Feedarr.Api.Models;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Feedarr.Api.Tests;

public sealed class ProviderResponseCacheTests
{
    [Fact]
    public async Task TenConcurrentRequestsUseOneFetchAndKeepKeysSeparate()
    {
        using var cache = new ProviderResponseCache();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        async Task<string?> Fetch(CancellationToken ct) { Interlocked.Increment(ref calls); await gate.Task.WaitAsync(ct); return "series"; }
        var tasks = Enumerable.Range(0, 10).Select(_ => cache.GetAsync("tv:query", Fetch, _ => true, default)).ToArray();
        Assert.Equal(1, calls);
        Assert.Equal("movie", await cache.GetAsync("movie:query", _ => Task.FromResult<string?>("movie"), _ => true, default));
        gate.SetResult();
        Assert.All(await Task.WhenAll(tasks), result => Assert.Equal("series", result));
        Assert.Equal("series", await cache.GetAsync("tv:query", Fetch, _ => true, default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExpiredAndIncompleteResponsesAreFetchedAgain()
    {
        using var cache = new ProviderResponseCache(TimeSpan.FromMilliseconds(30));
        int calls = 0;
        Task<string?> Fetch(CancellationToken _) => Task.FromResult<string?>((++calls).ToString());
        Assert.Equal("1", await cache.GetAsync("key", Fetch, _ => false, default));
        Assert.Equal("2", await cache.GetAsync("key", Fetch, _ => true, default));
        Assert.Equal("2", await cache.GetAsync("key", Fetch, _ => true, default));
        await Task.Delay(80);
        Assert.Equal("3", await cache.GetAsync("key", Fetch, _ => true, default));
    }

    [Fact]
    public async Task ErrorsAndNullResponsesAreNeverCached()
    {
        using var cache = new ProviderResponseCache();
        await Assert.ThrowsAsync<HttpRequestException>(() => cache.GetAsync<string>("key", _ => throw new HttpRequestException(), _ => true, default));
        Assert.Null(await cache.GetAsync<string>("key", _ => Task.FromResult<string?>(null), _ => true, default));
        Assert.Equal("valid", await cache.GetAsync("key", _ => Task.FromResult<string?>("valid"), _ => true, default));
    }

    [Fact]
    public async Task OneCancelledWaiterDoesNotCancelAnotherButLastWaiterCancelsFetch()
    {
        using var cache = new ProviderResponseCache();
        using var first = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        async Task<string?> Fetch(CancellationToken token) { observed = token; await gate.Task.WaitAsync(token); return "ok"; }
        var a = cache.GetAsync("key", Fetch, _ => true, first.Token);
        var b = cache.GetAsync("key", Fetch, _ => true, default);
        first.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        Assert.False(observed.IsCancellationRequested);
        gate.SetResult(); Assert.Equal("ok", await b);
        using var last = new CancellationTokenSource();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var c = cache.GetAsync<string>("other", async token => { using var registration = token.Register(() => cancelled.TrySetResult()); await Task.Delay(Timeout.Infinite, token); return "bad"; }, _ => true, last.Token);
        last.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("new", await cache.GetAsync("other", _ => Task.FromResult<string?>("new"), _ => true, default));
    }

    [Theory]
    [InlineData("tmdb", "tv/600?append_to_response=external_ids,alternative_titles", "{\"id\":600}")]
    [InlineData("tmdb", "tv/600/season/1/episode/2", "{\"id\":1,\"show_id\":700,\"season_number\":1,\"episode_number\":2,\"air_date\":\"2020-01-01\"}")]
    [InlineData("tvmaze", "shows/600", "{\"id\":700,\"name\":\"Show\",\"premiered\":null,\"externals\":{}}")]
    public void IncompleteAndForeignEvidenceIsNotReusable(string provider, string path, string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(VideoEvidenceResponse.Complete(provider, path, document.RootElement));
    }

    [Fact]
    public async Task TenIndependentReleasesKeepFreshPipelineDecisionsWithFewerTransportCalls()
    {
        int sharedSearch = 0, sharedProof = 0, freshSearch = 0, freshProof = 0, freshTvmaze = 0;
        static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        PosterMatchingContractTestRig Rig(bool shared) => new(PosterContractScenario.SeriesTvmazeMissTmdb, completeTmdbMetadata: true,
            tmdbResponse: (request, _) => {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.Contains("/search/")) { if (shared) sharedSearch++; else freshSearch++; }
                if (uri.AbsolutePath.Contains("/tv/600") && uri.Query.Contains("append_to_response"))
                {
                    if (shared) sharedProof++; else freshProof++;
                    return Task.FromResult<HttpResponseMessage?>(Json(new { id=600, name="Show Tmdb Hit", first_air_date="2018-01-01", seasons=new[] { new { season_number=1, episode_count=10 } }, external_ids=new { tvdb_id=8700 }, alternative_titles=new { results=Array.Empty<object>() } }));
                }
                if (uri.AbsolutePath.EndsWith("/season/1/episode/2"))
                {
                    if (shared) sharedProof++; else freshProof++;
                    return Task.FromResult<HttpResponseMessage?>(Json(new { id=602, show_id=600, season_number=1, episode_number=2, air_date="2018-01-02" }));
                }
                return Task.FromResult<HttpResponseMessage?>(null);
            });
        using var cached = Rig(true);
        for (int i=0; i<10; i++)
        {
            // Independent SQLite rows, same fixed provider observations, no historical association as truth.
            using var fresh = Rig(false);
            const string raw = "Show.Tmdb.Hit.2018.S01E02.1080p.WEB";
            var a = cached.CreateRelease(raw, "Show Tmdb Hit", 2018, UnifiedCategory.Serie, "series");
            var b = fresh.CreateRelease(raw, "Show Tmdb Hit", 2018, UnifiedCategory.Serie, "series");
            var actual = await cached.FetchAsync(a); var expected = await fresh.FetchAsync(b);
            Assert.True(expected.Ok, JsonSerializer.Serialize(expected.Body)); Assert.Equal(expected.StatusCode, actual.StatusCode);
            var ar = cached.GetReleaseForPoster(a)!; var br = fresh.GetReleaseForPoster(b)!;
            Assert.Equal(br.TmdbId, ar.TmdbId); Assert.Equal(600, ar.TmdbId);
            Assert.Equal(br.ExtProviderId, ar.ExtProviderId); Assert.Equal(br.ExtOverview, ar.ExtOverview);
            freshTvmaze += fresh.TvmazeMatchingRequests;
        }
        Assert.Equal(40, freshSearch); Assert.Equal(4, sharedSearch);
        Assert.Equal(20, freshProof); Assert.Equal(2, sharedProof);
        Assert.Equal(20, freshTvmaze); Assert.Equal(2, cached.TvmazeMatchingRequests);
    }
    [Fact]
    public async Task SearchTypeQueryLanguageAndPageStayIsolatedAndIncompletePagesAreNotCached()
    {
        int calls = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeMissTmdb, tmdbResponse: (request, _) => {
            if (!request.RequestUri!.AbsolutePath.Contains("/search/")) return Task.FromResult<HttpResponseMessage?>(null);
            calls++;
            var movie = request.RequestUri.AbsolutePath.Contains("movie");
            var json = JsonSerializer.Serialize(new { page=1, total_pages=1, total_results=1, results=new[] {
                new { id=movie ? 100 : 600, title="Movie", name="Series", release_date="1999-01-01", first_air_date="2018-01-01" }
            } });
            return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK) { Content=new StringContent(json, Encoding.UTF8, "application/json") });
        });
        Assert.Equal(600, (await rig.Tmdb.SearchVideoMatchingPageAsync("A", "fr-FR", 1, "tv", default))!.Results[0].TmdbId);
        Assert.Equal(100, (await rig.Tmdb.SearchVideoMatchingPageAsync("A", "fr-FR", 1, "movie", default))!.Results[0].TmdbId);
        await rig.Tmdb.SearchVideoMatchingPageAsync("B", "fr-FR", 1, "tv", default);
        await rig.Tmdb.SearchVideoMatchingPageAsync("A", "en-US", 1, "tv", default);
        Assert.Equal(4, calls);
        await rig.Tmdb.SearchVideoMatchingPageAsync("A", "fr-FR", 1, "tv", default);
        Assert.Equal(4, calls);
        Assert.Null(await rig.Tmdb.SearchVideoMatchingPageAsync("A", "fr-FR", 2, "tv", default));
        Assert.Null(await rig.Tmdb.SearchVideoMatchingPageAsync("A", "fr-FR", 2, "tv", default));
        Assert.Equal(6, calls);
    }

    [Fact]
    public async Task TvmazeSearchAndVerifiedDetailsAreSharedButDifferentPathsAreNot()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeHit);
        for (int i=0; i<10; i++)
        {
            var search = await rig.Tvmaze.GetMatchingEvidenceAsync("search/shows?q=Show%20Tvmaze%20Hit", default);
            Assert.Equal(3001, search!.Value[0].GetProperty("show").GetProperty("id").GetInt32());
            var show = await rig.Tvmaze.GetMatchingEvidenceAsync("shows/3001", default);
            Assert.Equal(3001, show!.Value.GetProperty("id").GetInt32());
        }
        Assert.Equal(2, rig.TvmazeMatchingRequests);
        await rig.Tvmaze.GetMatchingEvidenceAsync("search/shows?q=Different", default);
        Assert.Equal(3, rig.TvmazeMatchingRequests);
    }

}
