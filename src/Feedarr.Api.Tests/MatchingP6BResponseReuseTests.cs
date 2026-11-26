using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Xunit.Abstractions;

namespace Feedarr.Api.Tests;

public sealed class MatchingP6BResponseReuseTests(ITestOutputHelper output)
{
    private static JsonObject Show(int year=2000) => JsonSerializer.SerializeToNode(new {
        id=1,name="Example",premiered=$"{year}-01-01",externals=new{thetvdb=77},image=new{original="https://static.tvmaze.com/fr.jpg"},
        summary="Summary",genres=new[]{"Drama"},runtime=45
    })!.AsObject();
    private static HttpResponseMessage Json(JsonNode value)=>new(HttpStatusCode.OK){Content=new StringContent(value.ToJsonString())};

    [Theory][InlineData(false)][InlineData(true)]
    public async Task FullNeutralTvmazeSearchRecordsReplaceOnlyConcordantCompleteReads(bool partial)
    {
        var transports=0;var logical=0;
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeMissTmdb,tvmazeResponse:(request,_)=>{
            transports++;var path=request.RequestUri!.AbsolutePath;
            if(path.StartsWith("/search/")){var show=Show();if(partial)show.Remove("summary");return Task.FromResult<HttpResponseMessage?>(Json(new JsonArray(new JsonObject{["show"]=show})));}
            if(path.EndsWith("/episodes"))return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK){Content=new StringContent("[{\"id\":10,\"season\":1,\"number\":2,\"airdate\":\"2026-11-22\",\"_links\":{\"show\":{\"href\":\"https://api.tvmaze.com/shows/1\"}}}]")});
            return Task.FromResult<HttpResponseMessage?>(Json(Show()));
        });
        var queries=SearchCandidateGenerator.Generate(new("Example.S01E02.1080p.WEB.H264",UnifiedCategory.Serie));
        var diagnostics=new List<string>();
        var rows=await TelevisionCandidates.EnrichAsync(queries,UnifiedCategory.Serie,"Example.S01E02.1080p.WEB.H264",[],(provider,path,ct)=>{
            Assert.Equal("tvmaze",provider);logical++;return rig.Tvmaze.GetMatchingEvidenceAsync(path,ct);
        },true,default,diagnostics.Add);
        Assert.Single(rows);Assert.Equal(partial?3:2,logical);Assert.Equal(logical,transports);
        Assert.Contains($"reusedSearchDetails={(partial?0:1)}",diagnostics.Last());
        await rig.Tvmaze.GetMatchingEvidenceAsync("shows/1",default);Assert.Equal(logical,transports);
        await rig.Tvmaze.GetMatchingEvidenceAsync("shows/1",default,true);Assert.Equal(logical+1,transports);
        output.WriteLine($"P6B_REUSE tvmaze partial={partial} | baseline L=3 T=3 E=1 | after L={logical} T={logical} E=1 | publication transport=0 | forceRefresh transport=1");
    }

    [Fact]
    public async Task SearchSeedingCannotEraseAConflictingValidatedYear()
    {
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeMissTmdb,tvmazeResponse:(request,_)=>
            Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.StartsWith("/search/")?
                Json(new JsonArray(new JsonObject{["show"]=Show(2001)})):Json(Show())));
        await rig.Tvmaze.GetMatchingEvidenceAsync("shows/1",default);
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>rig.Tvmaze.GetMatchingEvidenceAsync("search/shows?q=Example",default));
        Assert.Equal("tv_provider_year_conflict",error.Message);
        var original=await rig.Tvmaze.GetMatchingEvidenceAsync("shows/1",default);Assert.Equal("2000-01-01",TelevisionCandidates.Text(original!.Value,"premiered"));
    }

    [Fact]
    public void ExtendedProjectionRequiresTypedPromisingEvidenceAndPreservesLocks()
    {
        var id=new SearchIdentity("tmdb",SearchMediaType.Series,"1");
        var row=new ProviderSearchCandidate(id,"Example",OfficialTitles:[new("Example",id,ProviderTitleKind.Original,ProviderTitleSource.SearchOriginalTitle)]);
        var context=new CandidateRankingContext([new("Example",null,CandidateYearEvidence.None,SearchCandidateOrigin.Primary)],UnifiedCategory.Serie,SearchMediaType.Series,Season:1);
        Assert.Equal(150,TelevisionCandidates.ProjectionLimit(context,[row]));
        Assert.Equal(100,TelevisionCandidates.ProjectionLimit(context with{Season=null},[row]));
        Assert.Equal(100,TelevisionCandidates.ProjectionLimit(context with{HasManualCorrection=true},[row]));
        Assert.Equal(100,TelevisionCandidates.ProjectionLimit(context with{EstablishedIdentity=id},[row]));
        Assert.Equal(100,TelevisionCandidates.ProjectionLimit(context with{ExpectedMediaType=SearchMediaType.Movie},[row]));
        Assert.Equal(100,new CandidateRankingPolicy().MaximumResults);
    }
}
