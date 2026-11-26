using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Tests;

public sealed class GameMatchingP3Tests
{
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) {
        Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json") };
    private static int Offset(HttpRequestMessage request) => int.Parse(Regex.Match(
        request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(), @"offset (\d+)").Groups[1].Value);
    private static JsonElement Rows(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private const string Bracketed = "Phantom.Blade.Zero.Deluxe.v302256-17280.[WIN.X64.MULTI.PORTABLE].[PREACTIVED].FRENCH";
    private const string Plain = "Phantom.Blade.Zero.Deluxe.v302256-17280.WIN.X64.MULTI.PORTABLE.PREACTIVED-NO-TAG";

    [Theory]
    [InlineData(Bracketed, "Phantom Blade Zero", "deluxe", null)]
    [InlineData(Plain, "Phantom Blade Zero", "deluxe", null)]
    [InlineData("Phantom.Blade.Zero.[Deluxe].v302256.WIN.X64-NoTag", "Phantom Blade Zero", "deluxe", null)]
    [InlineData("Blitzkrieg 3: Deluxe Edition (Build 26763333 + All DLCs)-[FitGirl Repack]", "Blitzkrieg 3", "deluxe", "included_dlcs")]
    [InlineData("Stellaris: Ultimate Bundle (v4.6.0 + 47 DLCs/Bonuses)-[FitGirl Repack]", "Stellaris", "ultimate bundle", "commercial_bundle")]
    [InlineData("Slime Rancher 2: Game & Soundtrack (v1.3.0 + Bonus OST)-[FitGirl Repack]", "Slime Rancher 2", null, "game_with_soundtrack")]
    [InlineData("Super.Mario.Odyssey.v1.4.1.RECOMPiLED.MULTi12-ElAmigos", "Super Mario Odyssey", null, null)]
    [InlineData("The.Legend.of.Zelda.Links.Awakening.v1.1.0.RECOMPiLED.MULTi11-ElAmigos", "The Legend of Zelda Links Awakening", null, null)]
    [InlineData("Flick-[FitGirl Repack]", "Flick", null, null)]
    public void AuditQueriesPreserveEditionAndDistributionScope(string raw,string query,string? edition,string? content)
    {
        var context=GameCandidateAdapter.Context(raw);
        Assert.Equal(query,Assert.Single(context.Queries).Title);
        Assert.Equal(edition,context.GameEdition);Assert.Equal(content,GameSearchScope.Read(raw).Content);
    }

    [Theory]
    [InlineData("Super.Mario.Odyssey.v1.4.1.RECOMPiLED.MULTi12-ElAmigos", "Super Mario Odyssey", 130, "recompiled")]
    [InlineData("The.Legend.of.Zelda.Links.Awakening.v1.1.0.RECOMPiLED.MULTi11-ElAmigos", "The Legend of Zelda Links Awakening", 130, "recompiled")]
    [InlineData("Game.PORTAGE.PS4.2026", "Game", 48, "console_port")]
    public void ConsoleOriginNeverSuppliesPcPlatformEvidence(string raw,string title,int platform,string origin)
    {
        var projected=GameCandidateAdapter.Project("igdb",JsonSerializer.SerializeToElement(new[]{new{id=1,name=title,platforms=new[]{platform}}}));
        var context=GameCandidateAdapter.Context(raw);
        var rank=SearchCandidateRanker.Rank(context,projected.Select(r=>r.Candidate).ToArray());
        Assert.NotEqual(CandidateDecision.Accept,rank.Decision);
        Assert.Contains(rank.Candidates.SelectMany(c=>c.Contradictions),c=>c.Code=="pc-platform-mismatch");
        Assert.Equal(origin,GameSearchScope.Read(raw).PlatformOrigin);
    }

    [Theory]
    [InlineData("Game Deluxe Edition", "Game", "game-edition-mismatch")]
    [InlineData("Game Ultimate Bundle (v1.0 + 4 DLCs)-[FitGirl Repack]", "Game", "game-edition-mismatch")]
    [InlineData("Game Remastered v1.0-CODEX", "Game", "game-edition-mismatch")]
    [InlineData("Game Remake v1.0-CODEX", "Game", "game-edition-mismatch")]
    [InlineData("Game DLC Named Expansion-voices38", "Game", "title-similarity-insufficient")]
    public void CleanerQueryCannotPromoteBaseGameAcrossContentScopes(string raw,string title,string reason)
    {
        var rows=GameCandidateAdapter.Project("igdb",JsonSerializer.SerializeToElement(new[]{new{id=1,name=title,platforms=new[]{6}}}));
        var ranking=SearchCandidateRanker.Rank(GameCandidateAdapter.Context(raw),rows.Select(r=>r.Candidate).ToArray());
        Assert.NotEqual(CandidateDecision.Accept,ranking.Decision);
        Assert.Contains(ranking.Candidates.SelectMany(c=>c.Contradictions),c=>c.Code==reason);
    }

    [Fact]
    public void MissingBlitzkriegEditionPlatformRemainsUnproven()
    {
        var rows=GameCandidateAdapter.Project("igdb",Rows("""
            [{"id":52653,"name":"Blitzkrieg 3: Deluxe Edition","version_title":"Deluxe","version_parent":10243},
             {"id":10243,"name":"Blitzkrieg 3","platforms":[6]}]
            """));
        var ranked=SearchCandidateRanker.Rank(GameCandidateAdapter.Context("Blitzkrieg 3: Deluxe Edition (Build 26763333 + All DLCs)-[FitGirl Repack]"),rows.Select(r=>r.Candidate).ToArray());
        Assert.NotEqual(CandidateDecision.Accept,ranked.Decision);
        Assert.Contains(ranked.Candidates.Single(c=>c.Identity.Id=="52653").Contradictions,c=>c.Code=="pc-platform-unavailable");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingSharedPhantomEditionsPublishWithoutReimportOrCategoryRewrite(bool directRss)
    {
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit,igdbResponse:(request,_)=> {
            if(request.RequestUri!.Host!="api.igdb.com")return Task.FromResult<HttpResponseMessage?>(null);
            return Task.FromResult<HttpResponseMessage?>(Json(Offset(request)>0?Rows("[]"):Rows("""
                [{"id":250618,"name":"Phantom Blade Zero","platforms":[6]},
                 {"id":413260,"name":"Phantom Blade Zero: Deluxe Edition","platforms":[6],"version_title":"Deluxe","version_parent":250618,"cover":{"url":"//images.igdb.com/igdb/upload/t_thumb/edition.jpg"}}]
                """)));
        });
        var first=rig.IngestRelease(Bracketed,UnifiedCategory.JeuWindows);
        var other=rig.IngestRelease(Plain,UnifiedCategory.JeuWindows);
        using(var db=rig.Database.Open()) {
            db.Execute("UPDATE sources SET source_kind=@kind WHERE id=1",new{kind=directRss?"rss":"torznab"});
            db.Execute("UPDATE releases SET entity_id=(SELECT entity_id FROM releases WHERE id=@first),poster_last_error='old_failure' WHERE id=@other",new{first,other});
        }
        Assert.True((await rig.FetchAsync(other)).Ok);
        Assert.Equal("413260",rig.GetReleaseForPoster(other)!.ExtProviderId);
        Assert.Equal("413260",rig.GetReleaseForPoster(first)!.ExtProviderId);
        Assert.Empty(rig.RawgSearchRequests);
    }

    [Fact]
    public async Task CapturedFlickWindowRetainsAll57IdentitiesAndExactCandidate()
    {
        var windows=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","Matching","igdb-flick-p3.json"))).RootElement.GetProperty("Windows");
        var data=windows[0].GetProperty("candidates");var calls=0;
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit,igdbResponse:(request,_)=> {
            if(request.RequestUri!.Host!="api.igdb.com")return Task.FromResult<HttpResponseMessage?>(null);
            calls++;Assert.Contains("limit 101;",request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            // Captured first/terminal windows; repeated first window models stability.
            return Task.FromResult<HttpResponseMessage?>(Json(Offset(request)>0?windows[1].GetProperty("candidates"):data));
        });
        var result=await rig.Igdb.ReadMatchingCandidatesAsync("Flick",default);
        Assert.Equal(57,result.GetArrayLength());Assert.Equal(3,calls);
        var ranking=SearchCandidateRanker.Rank(GameCandidateAdapter.Context("Flick-[FitGirl Repack]"),GameCandidateAdapter.Project("igdb",result,true).Select(r=>r.Candidate).ToArray());
        Assert.Equal("409649",ranking.SelectedIdentity?.Id);
    }

    [Fact]
    public async Task IdenticalConcurrentSearchesCoalesceAndCompletedEvidenceIsReused()
    {
        var calls=0;var started=new TaskCompletionSource();var proceed=new TaskCompletionSource();
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit,igdbResponse:async(request,ct)=> {
            if(request.RequestUri!.Host!="api.igdb.com")return null;
            Interlocked.Increment(ref calls);started.TrySetResult();await proceed.Task.WaitAsync(ct);
            return Json(Offset(request)>0?Rows("[]"):Rows("[{\"id\":1,\"name\":\"Game\",\"platforms\":[6]}]"));
        });
        var first=rig.Igdb.ReadMatchingCandidatesAsync("same",default);await started.Task;
        var second=rig.Igdb.ReadMatchingCandidatesAsync("same",default);proceed.SetResult();
        await Task.WhenAll(first,second);await rig.Igdb.ReadMatchingCandidatesAsync("same",default);
        Assert.Equal(3,calls);
        await rig.Igdb.ReadMatchingCandidatesAsync("different",default);Assert.Equal(6,calls);
    }

    [Fact]
    public async Task CoverageFailureIsNeverCachedAndNeverCallsRawg()
    {
        var calls=0;
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit,igdbResponse:(request,_)=> {
            if(request.RequestUri!.Host!="api.igdb.com")return Task.FromResult<HttpResponseMessage?>(null);
            calls++;return Task.FromResult<HttpResponseMessage?>(Json(Rows("[{\"id\":1,\"name\":\"Game\",\"platforms\":[6]}]")));
        });
        for(var i=0;i<2;i++) {
            var error=await Assert.ThrowsAsync<InvalidDataException>(()=>rig.Igdb.ReadMatchingCandidatesAsync("Game",default));
            Assert.Equal("terminal_probe_nonempty",error.Data["IgdbCoverageReason"]);
        }
        Assert.Equal(4,calls);Assert.Empty(rig.RawgSearchRequests);
    }
    [Theory]
    [InlineData("DLC / Addon", "dlc")]
    [InlineData("Expansion", "expansion")]
    [InlineData("Standalone Expansion", "standalone_expansion")]
    public void ProviderContentKindCannotBeMistakenForBaseGame(string kind,string edition)
    {
        var rows=GameCandidateAdapter.Project("igdb",JsonSerializer.SerializeToElement(new[]{new{id=1,name="Game",platforms=new[]{6},game_type=new{type=kind}}}));
        Assert.Equal(edition,Assert.Single(rows).Candidate.Game!.Edition);
        var ranking=SearchCandidateRanker.Rank(GameCandidateAdapter.Context("Game"),rows.Select(r=>r.Candidate).ToArray());
        Assert.Equal(CandidateDecision.Reject,ranking.Decision);
    }

    [Fact]
    public void DeluxeExpansionCannotHideItsContentKindBehindCommercialEdition()
    {
        var rows=GameCandidateAdapter.Project("igdb",Rows("""
            [{"id":1,"name":"Game Deluxe Edition","platforms":[6],"game_type":{"type":"Expansion"}}]
            """));
        Assert.Equal("deluxe|expansion",Assert.Single(rows).Candidate.Game!.Edition);
        Assert.Equal(CandidateDecision.Reject,SearchCandidateRanker.Rank(GameCandidateAdapter.Context("Game Deluxe Edition"),rows.Select(r=>r.Candidate).ToArray()).Decision);
    }

    [Fact]
    public async Task SharedTypedClientsReuseEvidenceButCredentialChangesDoNot()
    {
        using var state=new Feedarr.Api.Services.Igdb.IgdbMatchingState();var calls=0;
        Task<HttpResponseMessage?> Handle(HttpRequestMessage request,CancellationToken ct) {
            if(request.RequestUri!.Host!="api.igdb.com")return Task.FromResult<HttpResponseMessage?>(null);
            calls++;return Task.FromResult<HttpResponseMessage?>(Json(Rows("[]")));
        }
        using var first=new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit,igdbResponse:Handle,igdbMatchingState:state);
        using var second=new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit,igdbResponse:Handle,igdbMatchingState:state);
        await first.Igdb.ReadMatchingCandidatesAsync("same",default);
        await second.Igdb.ReadMatchingCandidatesAsync("same",default);Assert.Equal(1,calls);
        using(var db=second.Database.Open()) db.Execute("UPDATE external_provider_instances SET auth_json=@auth WHERE provider_key='igdb'",new{auth="{\"clientId\":\"different-test-id\",\"clientSecret\":\"different-test-secret\"}"});
        await second.Igdb.ReadMatchingCandidatesAsync("same",default);Assert.Equal(2,calls);
    }

    [Fact]
    public async Task OneCancelledConsumerDoesNotCancelAnotherSharedCollection()
    {
        var calls=0;var started=new TaskCompletionSource();var proceed=new TaskCompletionSource();
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit,igdbResponse:async(request,ct)=> {
            if(request.RequestUri!.Host!="api.igdb.com")return null;
            Interlocked.Increment(ref calls);started.TrySetResult();await proceed.Task.WaitAsync(ct);
            return Json(Rows("[]"));
        });
        using var cancellation=new CancellationTokenSource();
        var first=rig.Igdb.ReadMatchingCandidatesAsync("same",cancellation.Token);await started.Task;
        var second=rig.Igdb.ReadMatchingCandidatesAsync("same",default);
        cancellation.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);
        proceed.SetResult();await second;await rig.Igdb.ReadMatchingCandidatesAsync("same",default);
        Assert.Equal(1,calls);
    }

}
