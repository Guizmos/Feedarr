using System.Net;
using System.Text.Json;
using Feedarr.Api.Controllers;
using Feedarr.Api.Models;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Rawg;
using Microsoft.AspNetCore.Mvc;

namespace Feedarr.Api.Tests;

public sealed class RawgQuotaTests
{
    private const string Key = "rawg-test-key";
    private static HttpResponseMessage Json(string body, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(body) };
    private static HttpResponseMessage Empty() => Json("{\"count\":0,\"next\":null,\"results\":[]}");

    [Theory]
    [InlineData(401,"{\"error\":\"The monthly API limit reached\"}","rawg_quota_exhausted")]
    [InlineData(429,"{\"error\":\"The monthly API limit reached\"}","rawg_quota_exhausted")]
    [InlineData(401,"{\"error\":\"Invalid API key\"}","rawg_invalid_api_key")]
    [InlineData(401,"{\"error\":\"The API key is not found\"}","rawg_invalid_api_key")]
    [InlineData(401,"{\"error\":\"secret https://private/?key=hidden\"}","rawg_http_unauthorized")]
    [InlineData(429,"{}","rawg_http_rate_limited")]
    [InlineData(500,"{}","rawg_http_server_error")]
    [InlineData(403,"{}","rawg_http_forbidden")]
    public async Task ProviderErrorIsClassifiedWithoutRetainingBody(int status,string body,string expected)
    {
        using var response=Json(body,status);
        Assert.Equal(expected,await RawgFailurePolicy.ClassifyAsync(response,default));
    }

    [Fact]
    public async Task UnrecognizedOrOversizedBodyCannotDeclareQuotaOrLeakText()
    {
        using var response=Json("{\"error\":\"The monthly API limit reached private-marker\"}",401);
        Assert.Equal("rawg_http_unauthorized",await RawgFailurePolicy.ClassifyAsync(response,default));
        using var huge=Json(new string('x',9000),401);
        Assert.Equal("rawg_http_unauthorized",await RawgFailurePolicy.ClassifyAsync(huge,default));
    }

    [Fact]
    public async Task ConcurrentDifferentTitlesStopAfterFirstQuotaResponseAndReadOnlyStatusDoesNotProbe()
    {
        var calls=0;
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit,rawgResponse:async(_,ct)=>{
            Interlocked.Increment(ref calls); await Task.Delay(10,ct);
            return Json("{\"error\":\"The monthly API limit reached\"}",401);
        });
        var errors=await Task.WhenAll(Enumerable.Range(0,12).Select(async i=>await Assert.ThrowsAsync<InvalidDataException>(()=>rig.Rawg.ReadMatchingCandidatesAsync("game "+i,default))));
        Assert.Equal(1,calls);
        Assert.All(errors,e=>Assert.Equal("rawg_quota_exhausted",e.Data["RawgFailureReason"]));
        Assert.Equal(11,errors.Count(e=>Equals(true,e.Data["RawgRequestSuppressed"])));
        var controller=new ExternalProvidersController(new ExternalProviderRegistry(),rig.ExternalInstances,null!,rig.RawgState);
        var payload=JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(controller.Get()).Value,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var rawg=payload.GetProperty("instances").EnumerateArray().Single(r=>r.GetProperty("providerKey").GetString()=="rawg");
        Assert.Equal("quota_exhausted",rawg.GetProperty("availability").GetProperty("status").GetString());
        Assert.True(rawg.GetProperty("availability").GetProperty("suspended").GetBoolean());
        Assert.DoesNotContain(Key,payload.GetRawText());Assert.DoesNotContain("ENC:",payload.GetRawText());
        Assert.Equal(1,calls);
    }

    [Fact]
    public async Task ManualProbeIsSingleFlightThrottledAndOnlySuccessReactivates()
    {
        var calls=0;
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit,rawgResponse:(_,_)=>{
            Interlocked.Increment(ref calls); return Task.FromResult<HttpResponseMessage?>(Empty());
        });
        rig.RawgState.Failure(Key,"rawg_quota_exhausted");
        Assert.True(await rig.Rawg.TestApiAsync(default));
        Assert.Equal("ready",rig.RawgState.GetStatus(Key).Status);
        Assert.False(await rig.Rawg.TestApiAsync(default));Assert.Equal(1,calls);
        await rig.Rawg.ReadMatchingCandidatesAsync("after manual test",default);Assert.Equal(2,calls);
    }

    [Fact]
    public async Task FailedProbePreservesQuotaAndNeverCallsRawgOnAutomaticSearches()
    {
        var calls=0;
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit,rawgResponse:(_,_)=>{
            calls++; return Task.FromResult<HttpResponseMessage?>(Json("{\"error\":\"The monthly API limit reached\"}",401));
        });
        rig.RawgState.Failure(Key,"rawg_quota_exhausted");
        Assert.False(await rig.Rawg.TestApiAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(()=>rig.Rawg.ReadMatchingCandidatesAsync("game",default));
        Assert.Equal(1,calls); Assert.Equal("quota_exhausted",rig.RawgState.GetStatus(Key).Status);
        Assert.False(await rig.Rawg.TestApiAsync(default));Assert.Equal(1,calls);
    }

    [Fact]
    public void ProbeCooldownIsPerCredentialAndDoesNotResetOtherInstancesQuota()
    {
        var clock=new Clock();using var state=new RawgAuthenticationState(clock);
        state.Failure("one","rawg_quota_exhausted");Assert.True(state.TryStartProbe("one"));
        Assert.False(state.TryStartProbe("one"));Assert.True(state.TryStartProbe("two"));state.Accept("two");
        Assert.Equal("quota_exhausted",state.GetStatus("one").Status);
        clock.Now=clock.Now.AddSeconds(31);Assert.True(state.TryStartProbe("one"));
        Assert.Equal("quota_exhausted",state.GetStatus("one").Status); // time never guesses a monthly reset
    }

    [Fact]
    public async Task InvalidSuccessfulProbeCannotReactivateQuota()
    {
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit,
            rawgResponse:(_,_)=>Task.FromResult<HttpResponseMessage?>(Json("{\"error\":\"private-marker\"}")));
        rig.RawgState.Failure(Key,"rawg_quota_exhausted");
        Assert.False(await rig.Rawg.TestApiAsync(default));
        Assert.Equal("quota_exhausted",rig.RawgState.GetStatus(Key).Status);
    }

    [Fact]
    public async Task TemporaryRecoveryFailureCannotReenableAnExhaustedQuota()
    {
        var calls=0;
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit,rawgResponse:(_,_)=>{
            calls++;return Task.FromResult<HttpResponseMessage?>(Json("{}",503));
        });
        rig.RawgState.Failure(Key,"rawg_quota_exhausted");
        Assert.False(await rig.Rawg.TestApiAsync(default));
        Assert.True(rig.RawgState.GetStatus(Key).Suspended);
        Assert.Equal("quota_exhausted",rig.RawgState.GetStatus(Key).Status);
        await Assert.ThrowsAsync<InvalidDataException>(()=>rig.Rawg.ReadMatchingCandidatesAsync("game",default));
        Assert.Equal(1,calls);
    }

    [Fact]
    public async Task SharedCompleteResponseIsReusedAcrossIdenticalSearchesAndCredentialsStaySeparate()
    {
        var calls=0;var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit,rawgResponse:async(_,ct)=>{
            Interlocked.Increment(ref calls);await release.Task.WaitAsync(ct);return Empty();
        });
        var requests=Enumerable.Range(0,10).Select(_=>rig.Rawg.ReadMatchingCandidatesAsync("same exact query",default)).ToArray();
        release.SetResult();await Task.WhenAll(requests);
        await rig.Rawg.ReadMatchingCandidatesAsync("same exact query",default);Assert.Equal(1,calls);
        rig.RawgState.Failure(Key,"rawg_quota_exhausted");
        await rig.Rawg.ReadMatchingCandidatesAsync("same exact query",default);Assert.Equal(1,calls); // safe cached evidence, zero network
        Assert.Equal("quota_exhausted",rig.RawgState.GetStatus(Key).Status);
        await Assert.ThrowsAsync<InvalidDataException>(()=>rig.Rawg.ReadMatchingCandidatesAsync("different query",default));
        Assert.Equal(1,calls);
    }

    [Fact]
    public async Task OneCancelledConsumerDoesNotCancelAnotherOrDuplicateTheSharedRequest()
    {
        var calls=0;var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit,rawgResponse:async(_,ct)=>{
            calls++;started.TrySetResult();await release.Task.WaitAsync(ct);return Empty();
        });
        using var cancellation=new CancellationTokenSource();
        var first=rig.Rawg.ReadMatchingCandidatesAsync("shared query",cancellation.Token);
        await started.Task;
        var second=rig.Rawg.ReadMatchingCandidatesAsync("shared query",default);
        cancellation.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);
        release.SetResult();await second;
        await rig.Rawg.ReadMatchingCandidatesAsync("shared query",default);
        Assert.Equal(1,calls);
    }

    [Fact]
    public async Task PartialMatchingPagesAreNeverCachedOrConfusedWithManualSearches()
    {
        var calls=0;
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit,rawgResponse:(_,_)=>{
            calls++;return Task.FromResult<HttpResponseMessage?>(Json("{\"count\":40,\"next\":\"https://api.rawg.io/api/games?page=2\",\"results\":[]}"));
        });
        await rig.Rawg.ReadMatchingCandidatesAsync("game",default);
        await rig.Rawg.ReadMatchingCandidatesAsync("game",default);Assert.Equal(2,calls);
        await rig.Rawg.SearchGameListAsync("game",default);Assert.Equal(3,calls);
        await rig.Rawg.ReadMatchingCandidatesAsync("game",default);Assert.Equal(4,calls);
    }

    [Fact]
    public async Task TemporaryFailureDoesNotLatchQuotaAndQuotaFailurePreservesIgdbEvidence()
    {
        var temporary=true;var calls=0;
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit,
            igdbResponse:(request,_)=>Task.FromResult<HttpResponseMessage?>(request.RequestUri!.Host=="api.igdb.com"?Json("[]"):null),
            rawgResponse:(_,_)=>{calls++;return Task.FromResult<HttpResponseMessage?>(temporary?Json("{}",503):Json("{\"error\":\"The monthly API limit reached\"}",401));});
        await Assert.ThrowsAsync<InvalidDataException>(()=>rig.Rawg.ReadMatchingCandidatesAsync("first",default));
        Assert.Equal("temporary_error",rig.RawgState.GetStatus(Key).Status);Assert.False(rig.RawgState.IsRejected(Key));
        temporary=false;var id=rig.IngestRelease("Game Hit",UnifiedCategory.JeuWindows);var result=await rig.FetchAsync(id);
        Assert.False(result.Retryable);Assert.Equal(2,calls);
        var error=rig.GetReleaseForPoster(id)!.PosterLastError!;
        Assert.Contains("rawg_quota_exhausted",error);Assert.Contains("no-provider-results",error);
        Assert.Null(rig.GetReleaseForPoster(id)!.ExtProviderId);Assert.Null(rig.GetReleaseForPoster(id)!.PosterFile);
        Assert.DoesNotContain(Key,error);
    }

    [Fact]
    public async Task AcceptedIgdbResultIsNotDisabledByRawgQuota()
    {
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit,
            rawgResponse:(_,_)=>throw new InvalidOperationException("No RAWG request allowed"));
        rig.RawgState.Failure(Key,"rawg_quota_exhausted");
        var id=rig.IngestRelease("Game Hit",UnifiedCategory.JeuWindows);
        Assert.True((await rig.FetchAsync(id)).Ok);Assert.Equal("igdb",rig.GetReleaseForPoster(id)!.ExtProvider);
    }

    private sealed class Clock:TimeProvider { internal DateTimeOffset Now=DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow()=>Now; }
}
