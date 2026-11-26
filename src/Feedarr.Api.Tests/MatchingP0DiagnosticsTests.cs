using System.Net;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Rawg;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Data.Repositories;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.VisualBasic.FileIO;
using O = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class MatchingP0DiagnosticsTests
{
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private static Task<HttpResponseMessage?> EmptyIgdb(HttpRequestMessage request, CancellationToken _) =>
        Task.FromResult<HttpResponseMessage?>(request.RequestUri!.Host == "api.igdb.com" ? Json("[]") : null);
    private static PosterFetchJob Job(long id, string title, UnifiedCategory category, string file) => new(id, title, null, category, false, 0, null, file);
    private static (PosterFetchJobProcessor Processor, RetroFetchLogService Csv) Worker(PosterMatchingContractTestRig rig)
    {
        var root = Path.GetDirectoryName(rig.Database.DbPath)!;
        var csv = new RetroFetchLogService(O.Create(new AppOptions { DataDir = root }), new Environment(root));
        return (new(NullLogger<PosterFetchJobProcessor>.Instance, new PosterFetchQueue(NullLogger<PosterFetchQueue>.Instance),
            rig.Posters, rig.Repository, csv, O.Create(new PosterFetchOptions { MaxAttempts = 3, MaxItemDurationSeconds = 20, RequestTimeoutSeconds = 15, RetryDelaysSeconds = [1] })), csv);
    }
    private static List<Dictionary<string,string>> Read(string path)
    {
        using var parser = new TextFieldParser(path); parser.SetDelimiters(","); parser.HasFieldsEnclosedInQuotes = true;
        var header = parser.ReadFields()!; var rows = new List<Dictionary<string,string>>();
        while (!parser.EndOfData) { var row = parser.ReadFields()!; Assert.Equal(header.Length, row.Length); rows.Add(header.Zip(row).ToDictionary(x => x.First, x => x.Second)); }
        return rows;
    }

    [Fact]
    public async Task UnauthorizedRawgStopsWorkerRetriesAndReportsActualProvidersAndIgdbRanking()
    {
        var calls = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit, igdbResponse: EmptyIgdb,
            rawgResponse: (_, _) => { calls++; return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.Unauthorized) { Content = new StringContent("private-key-marker") }); });
        var worker = Worker(rig); var file = worker.Csv.CreateRetroFetchLog();
        var id = rig.IngestRelease("Game Hit", UnifiedCategory.JeuWindows);
        Assert.False((await worker.Processor.ProcessJobAsync(Job(id,"Game Hit",UnifiedCategory.JeuWindows,file),1,default)).Succeeded);
        Assert.Equal(1, calls);
        var row = Assert.Single(Read(worker.Csv.ResolveLogPath(file)!));
        Assert.Equal(id.ToString(),row["releaseId"]); Assert.Equal("igdb+rawg",row["provider"]);
        Assert.True(DateTimeOffset.TryParse(row["attemptedAtUtc"],out _));
        Assert.Equal("igdb_then_rawg",row["strategy"]);
        Assert.Contains("rawg_http_unauthorized",row["reason"]);
        Assert.Contains("no-provider-results",row["ranking"]);
        Assert.Contains("complete",row["coverage"]);
        Assert.DoesNotContain("private-key-marker",File.ReadAllText(worker.Csv.ResolveLogPath(file)!));
        Assert.DoesNotContain("rawg-test-key",File.ReadAllText(worker.Csv.ResolveLogPath(file)!));
        var second = rig.IngestRelease("Another Game",UnifiedCategory.JeuWindows);
        Assert.False((await worker.Processor.ProcessJobAsync(Job(second,"Another Game",UnifiedCategory.JeuWindows,file),1,default)).Succeeded);
        Assert.Equal(1,calls); // Same rejected key must not be sent by another release.
        Assert.Equal("igdb",Read(worker.Csv.ResolveLogPath(file)!)[1]["provider"]);
    }

    [Fact]
    public async Task CredentialRotationIsImmediatelyUsableAndExplicitTestCanRecoverSameKey()
    {
        var unauthorized = true; var calls = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit,
            rawgResponse: (_, _) => { calls++; return Task.FromResult<HttpResponseMessage?>(unauthorized ? new(HttpStatusCode.Unauthorized) : Json("{\"count\":0,\"next\":null,\"results\":[]}")); });
        await Assert.ThrowsAsync<InvalidDataException>(()=>rig.Rawg.ReadMatchingCandidatesAsync("one",default));
        var blocked = await Assert.ThrowsAsync<InvalidDataException>(()=>rig.Rawg.ReadMatchingCandidatesAsync("two",default));
        Assert.Equal(true,blocked.Data["RawgRequestSuppressed"]); Assert.Equal(1,calls);
        using (var conn=rig.Database.Open()) conn.Execute("UPDATE external_provider_instances SET auth_json=@auth WHERE provider_key='rawg'",new { auth=JsonSerializer.Serialize(new { apiKey="rotated-test-key" }) });
        unauthorized=false;
        await rig.Rawg.ReadMatchingCandidatesAsync("three",default); Assert.Equal(2,calls);
        unauthorized=true;
        await Assert.ThrowsAsync<InvalidDataException>(()=>rig.Rawg.ReadMatchingCandidatesAsync("four",default));
        unauthorized=false;
        Assert.True(await rig.Rawg.TestApiAsync(default));
        await rig.Rawg.ReadMatchingCandidatesAsync("five",default); Assert.Equal(5,calls);
    }

    [Fact]
    public async Task UiConnectionTestReportsUnauthorizedAndCanClearSharedAuthenticationState()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit);
        var clock=new ManualClock(); var state=new RawgAuthenticationState(clock); var ok=false; const string key="secret-config-marker";
        var stats=new ProviderStatsService(new StatsRepository(rig.Database,new MemoryCache(new MemoryCacheOptions())));
        var factory=new Factory(()=>new HttpClient(new Handler((_,_)=>Task.FromResult(ok?Json("{\"count\":0,\"results\":[]}"):new HttpResponseMessage(HttpStatusCode.Unauthorized)))));
        var service=new ExternalProviderTestService(factory,stats,NullLogger<ExternalProviderTestService>.Instance,state);
        var auth=new Dictionary<string,string?> { ["apiKey"]=key };
        var failed=await service.TestAsync("rawg",null,auth,default);
        Assert.False(failed.Ok); Assert.Equal("rawg_http_unauthorized",failed.Error); Assert.True(state.IsRejected(key));
        Assert.False(state.IsRejected("different-key"));
        clock.Now=clock.Now.AddSeconds(31);
        ok=true; Assert.True((await service.TestAsync("rawg",null,auth,default)).Ok); Assert.False(state.IsRejected(key));
    }

    [Fact]
    public async Task UnprovenRoutingPersistsReasonDateAndRetroFieldsWithoutCallingAProvider()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit,
            tmdbResponse: (_,_)=>throw new InvalidOperationException("No provider allowed"));
        var id=rig.IngestRelease("Lily.C.A.T.JAP.VOSTEN.480p.DVDRip.AAC.2.0.x264-NoTag",UnifiedCategory.Animation);
        using(var c=rig.Database.Open()) {
            c.Execute("UPDATE sources SET source_kind='rss' WHERE id=1");
            c.Execute("UPDATE releases SET category_id=900,category_ids='900',std_category_id=NULL,spec_category_id=NULL WHERE id=@id",new{id});
        }
        var worker=Worker(rig); var file=worker.Csv.CreateRetroFetchLog();
        Assert.False((await worker.Processor.ProcessJobAsync(Job(id,"Lily C A T",UnifiedCategory.Animation,file),1,default)).Succeeded);
        var saved=rig.GetReleaseForPoster(id)!;
        Assert.StartsWith("v2:routing:business_abstention:video_type_unproven_or_conflicting",saved.PosterLastError);
        Assert.Contains("routingReason=direct_rss_parsed_year_unavailable",saved.PosterLastError);
        Assert.NotNull(saved.PosterLastAttemptTs); Assert.Null(saved.TmdbId); Assert.Null(saved.PosterFile);
        var row=Assert.Single(Read(worker.Csv.ResolveLogPath(file)!));
        Assert.Equal("none",row["provider"]); Assert.Equal("none",row["strategy"]); Assert.Equal("routing",row["stage"]);
        Assert.Contains("video_type_unproven_or_conflicting",row["reason"]); Assert.DoesNotContain("unknown",row["reason"]);
    }

    [Fact]
    public async Task BudgetFailureRecordsIncompleteTargetedPlanWithoutAFalseRanking()
    {
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit,
            tmdbResponse:(request,_)=>request.RequestUri!.AbsolutePath.EndsWith("/search/movie")
                ? Task.FromResult<HttpResponseMessage?>(Json(JsonSerializer.Serialize(new{page=1,total_pages=20,total_results=400,
                    results=Enumerable.Range(1,20).Select(id=>new{id,title="Film",original_title="Film",release_date="2025-01-01"})})))
                :Task.FromResult<HttpResponseMessage?>(null));
        var id=rig.IngestRelease("Film.2025.1080p.WEB",UnifiedCategory.Film);
        var worker=Worker(rig);var file=worker.Csv.CreateRetroFetchLog();
        Assert.False((await worker.Processor.ProcessJobAsync(Job(id,"Film",UnifiedCategory.Film,file),1,default)).Succeeded);
        var row=Assert.Single(Read(worker.Csv.ResolveLogPath(file)!));
        Assert.Equal("tmdb",row["provider"]);Assert.Equal("targeted_year",row["strategy"]);
        Assert.Contains("not_evaluable_search_budget",row["coverage"]);Assert.Equal("",row["ranking"]);
    }

    [Fact]
    public void LimitedRankingContainsScoresAndReasonsWithoutCandidateTextOrUnsafeIdentities()
    {
        var ctx=new CandidateRankingContext(SearchCandidateGenerator.Generate(new("Film.2025.1080p.WEB",UnifiedCategory.Film)),UnifiedCategory.Film,SearchMediaType.Movie);
        var candidates=Enumerable.Range(1,10).Select(i=>new ProviderSearchCandidate(new("tmdb",SearchMediaType.Movie,i.ToString()),
            i==10?"https://private.test/?passkey=private-marker":"Film",2025)).ToArray();
        var ranked=SearchCandidateRanker.Rank(ctx,candidates);var logger=new Logger();
        using var scope=MatchingAttemptDiagnostics.Begin(42);
        MatchingAttemptDiagnostics.LogRanking(logger,42,ranked);
        var json=JsonDocument.Parse(scope.Ranking!).RootElement;
        Assert.Equal(10,json.GetProperty("CandidateCount").GetInt32());Assert.True(json.GetProperty("Truncated").GetBoolean());
        Assert.Equal(6,json.GetProperty("Candidates").GetArrayLength());
        Assert.Contains("close-competing-identities",scope.Ranking);Assert.DoesNotContain("private-marker",scope.Ranking);
        Assert.Contains("Confidence",scope.Ranking); Assert.Equal(JsonSerializer.Serialize(ranked),JsonSerializer.Serialize(SearchCandidateRanker.Rank(ctx,candidates)));
    }

    [Fact]
    public async Task ConcurrentScopesAndCachedWorkDoNotInventOrMixActualProviders()
    {
        using var parent=MatchingAttemptDiagnostics.Begin(1);
        var results=await Task.WhenAll(new[]{"tmdb","rawg"}.Select(async provider=> {
            using var scope=MatchingAttemptDiagnostics.Begin(provider=="tmdb"?2:3);
            using var client=new HttpClient(new MatchingProviderRequestHandler(new Handler((_,_)=>Task.FromResult(Json("{}")))));
            await client.GetAsync(provider=="tmdb"?"https://api.themoviedb.org/3/search/movie?api_key=secret":"https://api.rawg.io/api/games?key=secret");
            return scope.Providers;
        }));
        Assert.Equal(new[]{"tmdb","rawg"},results); Assert.Equal("none",parent.Providers);
    }

    [Fact]
    public async Task CsvPreservesLegacyFormatRedactsSecretsAndSerializesConcurrentWrites()
    {
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);var csv=Worker(rig).Csv;
        var file=csv.CreateRetroFetchLog();
        await Task.WhenAll(Enumerable.Range(1,20).Select(id=>Task.Run(()=>csv.AppendFailure(file,
            new("Films","movie","tmdb","Film https://host.test/?key=private-marker passkey=private-marker","rejected",id,DateTimeOffset.UtcNow,"targeted_year","complete","ranking")))));
        var rows=Read(csv.ResolveLogPath(file)!);Assert.Equal(20,rows.Count);Assert.Equal(20,rows.Select(r=>r["releaseId"]).Distinct().Count());
        Assert.DoesNotContain("private-marker",File.ReadAllText(csv.ResolveLogPath(file)!));
        var old="old.csv";File.WriteAllText(csv.ResolveLogPath(old)!,"category,mediaType,provider,query,reason\n");
        csv.AppendFailure(old,new("Films","movie","tmdb","Film","rejected",1,DateTimeOffset.UtcNow,"general","complete"));
        Assert.Equal(5,Assert.Single(Read(csv.ResolveLogPath(old)!)).Count);
    }

    [Theory]
    [InlineData("2040,5040", false, "conflicting_standard_families")]
    [InlineData("3040", false, "non_video_standard_family")]
    [InlineData("2040", false, "movie_family_conflicts_with_tv_evidence")]
    [InlineData("900", true, "direct_rss_parsed_year_unavailable")]
    public void RoutingDiagnosisExplainsRefusalWithoutChangingResolution(string ids,bool direct,string expected)
    {
        var row=new ReleaseForPoster { Title=ids=="2040"?"Show.S01.1080p.WEB":"Unknown.1080p.WEB",CategoryIds=ids,MediaType="movie",TmdbMediaType=ids=="2040"?"movie":null,IsDirectRss=direct };
        Assert.Null(PosterFetchService.ResolveVideoType(row,UnifiedCategory.Animation,out var reason));
        Assert.Equal(expected,reason);
        Assert.Null(PosterFetchService.ResolveVideoType(row,UnifiedCategory.Animation));
    }

    [Fact]
    public void NewAttemptDropsPriorRankingCoverageAndProviders()
    {
        using var scope=MatchingAttemptDiagnostics.Begin(123);
        scope.ProviderCalled("rawg");scope.Collection("general","complete");scope.SetOutcome("ranking","ambiguous");
        using (var next = MatchingAttemptDiagnostics.Begin(123))
        {
            Assert.Equal("none",next.Providers);Assert.Null(next.Ranking);Assert.Equal("none",next.Strategy);
            Assert.Equal("routing",next.Stage);Assert.Equal("not_started_or_not_recorded",next.Coverage);
        }
        Assert.Equal("rawg",scope.Providers);
    }

    private sealed class ManualClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Factory(Func<HttpClient> create):IHttpClientFactory {public HttpClient CreateClient(string name)=>create();}
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>send(r,ct);}
    private sealed class Logger:ILogger {
        public IDisposable? BeginScope<TState>(TState s) where TState:notnull=>null;public bool IsEnabled(LogLevel l)=>true;
        public void Log<TState>(LogLevel l,EventId id,TState s,Exception? e,Func<TState,Exception?,string> f) { }
    }
    private sealed class Environment(string root):IWebHostEnvironment {
        public string ApplicationName{get;set;}="Feedarr.Tests"; public string EnvironmentName{get;set;}="Test";
        public string ContentRootPath{get;set;}=root;public string WebRootPath{get;set;}=root;
        public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();
    }
}
