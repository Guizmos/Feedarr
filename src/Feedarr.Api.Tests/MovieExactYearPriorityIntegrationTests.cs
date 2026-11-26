using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Posters;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using static Feedarr.Api.Tests.MovieExactYearPriorityTests;

namespace Feedarr.Api.Tests;

public sealed class MovieExactYearPriorityIntegrationTests
{
    public static IEnumerable<object[]> CasesBySource => Cases.SelectMany(c=>new[]{"rss","torznab"}.Select(s=>new object[]{c.ReleaseId,s,Enum.Parse<UnifiedCategory>(c.Category)}))
        .Concat(new[]{UnifiedCategory.Animation,UnifiedCategory.Spectacle,UnifiedCategory.Autre}.SelectMany(category=>new[]{"rss","torznab"}.Select(source=>new object[]{32718L,source,category})));
    private static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value))};

    [Theory]
    [MemberData(nameof(CasesBySource))]
    public async Task VerifiedPriorityUsesExistingPublicationForEitherSource(long auditId,string source,UnifiedCategory category)
    {
        var c=Cases.Single(c=>c.ReleaseId==auditId);
        var logger=new CaptureLogger();var decisionCalls=0;
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit,completeTmdbMetadata:true,posterLogger:logger,
            tmdbResponse:(request,_)=>{
                var path=request.RequestUri!.AbsolutePath;var query=QueryHelpers.ParseQuery(request.RequestUri.Query);
                if(path.EndsWith("/search/movie")) {
                    decisionCalls++;var year=int.Parse(query["primary_release_year"]!);var language=query["language"].ToString();
                    var results=c.Candidates.Where(p=>p.Year==year).Select(p=>new {
                        id=int.Parse(p.Identity.Id),title=p.OfficialTitles!.FirstOrDefault(t=>t.Language==language&&t.Kind==Feedarr.Api.Services.Matching.ProviderTitleKind.Localized)?.Title??p.Title,
                        original_title=p.OfficialTitles!.First(t=>t.Kind==Feedarr.Api.Services.Matching.ProviderTitleKind.Original).Title,
                        release_date=$"{p.Year}-01-01",poster_path="/fixture.jpg",original_language="en"
                    }).ToArray();
                    return Task.FromResult<HttpResponseMessage?>(Json(new{page=1,total_pages=results.Length==0?0:1,total_results=results.Length,results}));
                }
                if(query.GetValueOrDefault("append_to_response").ToString().Contains("release_dates")) {
                    Assert.True(request.Options.TryGetValue(Feedarr.Api.Services.Resilience.TransientHttpRetryHandler.DisableRetries,out var disabled)&&disabled);
                    decisionCalls++;var p=c.Candidates.Single(p=>p.Identity.Id==path.Split('/').Last());
                    return Task.FromResult<HttpResponseMessage?>(Json(History(p)));
                }
                if(path.EndsWith("/alternative_titles")) {
                    decisionCalls++;var p=c.Candidates.Single(p=>p.Identity.Id==path.Split('/')[^2]);return Task.FromResult<HttpResponseMessage?>(Json(Aliases(p)));
                }
                if(path.EndsWith("/movie/"+c.SelectedId)) {
                    var obj=JsonNode.Parse(History(c.Candidates.Single(p=>p.Identity.Id==c.SelectedId)).GetRawText())!.AsObject();
                    obj["overview"]="Verified fixture synopsis";obj["poster_path"]="/fixture.jpg";obj["runtime"]=100;
                    return Task.FromResult<HttpResponseMessage?>(Json(obj));
                }
                return Task.FromResult<HttpResponseMessage?>(null);
            });
        var id=rig.IngestRelease(c.RawTitle,category);
        using(var conn=rig.Database.Open()) {
            conn.Execute("UPDATE sources SET source_kind=@source WHERE id=1",new{source});
            if(source=="rss"&&category!=UnifiedCategory.Autre)conn.Execute("UPDATE releases SET category_id=@cat,category_ids=@ids,std_category_id=NULL,spec_category_id=NULL WHERE id=@id",new{id,cat=category==UnifiedCategory.Animation?900:category==UnifiedCategory.Spectacle?909:906,ids=category==UnifiedCategory.Animation?"900":category==UnifiedCategory.Spectacle?"909":"906"});
        }
        var before=rig.GetReleaseForPoster(id)!;
        var result=await rig.FetchAsync(id);Assert.True(result.Ok,JsonSerializer.Serialize(result.Body)+" | "+rig.GetReleaseForPoster(id)!.PosterLastError+" | "+JsonSerializer.Serialize(logger.Events));
        var saved=rig.GetReleaseForPoster(id)!;Assert.Equal(int.Parse(c.SelectedId),saved.TmdbId);Assert.Equal("movie",saved.TmdbMediaType);
        Assert.NotNull(saved.PosterFile);Assert.Equal(before.UnifiedCategory,saved.UnifiedCategory);Assert.Equal(before.CategoryIds,saved.CategoryIds);
        Assert.Equal(before.Year,saved.Year);Assert.Equal(before.TitleClean,saved.TitleClean);
        Assert.InRange(decisionCalls,6,12);
        Assert.Contains(logger.Events,e=>Equals(e.GetValueOrDefault("PriorityOutcome"),"applied")&&Equals(e.GetValueOrDefault("PriorityReason"),MovieExactYearPriority.DecisionReason));
        var calls=decisionCalls;Assert.True((await rig.FetchAsync(id)).Ok);Assert.Equal(calls,decisionCalls);
    }

    [Fact]
    public async Task ExactHomonymOnLastPageBlocksPriorityBeforePublication()
    {
        var logger=new CaptureLogger();var proofCalls=0;
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit,completeTmdbMetadata:true,posterLogger:logger,
            tmdbResponse:(request,_)=>{
                var path=request.RequestUri!.AbsolutePath;
                if(path.EndsWith("/search/movie")) {
                    var query=QueryHelpers.ParseQuery(request.RequestUri.Query);var year=int.Parse(query["primary_release_year"]!);var page=int.Parse(query["page"]!);
                    if(year!=2025)return Task.FromResult<HttpResponseMessage?>(Json(new{page=1,total_pages=0,total_results=0,results=Array.Empty<object>()}));
                    var results=Enumerable.Range(page==1?1:21,page==1?20:1).Select(id=>new{id,title=id is 1 or 21?"Movie":"Unrelated "+id,original_title=id is 1 or 21?"Movie":"Unrelated "+id,release_date="2025-01-01"}).ToArray();
                    return Task.FromResult<HttpResponseMessage?>(Json(new{page,total_pages=2,total_results=21,results}));
                }
                if(QueryHelpers.ParseQuery(request.RequestUri.Query).GetValueOrDefault("append_to_response").ToString().Contains("release_dates"))proofCalls++;
                return Task.FromResult<HttpResponseMessage?>(null);
            });
        var id=rig.IngestRelease("Movie.2025.1080p.WEB",UnifiedCategory.Film);
        Assert.False((await rig.FetchAsync(id)).Ok);Assert.Null(rig.GetReleaseForPoster(id)!.TmdbId);Assert.Equal(0,proofCalls);
        Assert.Contains(logger.Events,e=>Equals(e.GetValueOrDefault("PriorityOutcome"),"exact_year_identity_not_unique"));
    }

    [Fact]
    public async Task CombinedProofAndAliasesUseTheExistingResponseCache()
    {
        var c=Cases.First();var calls=0;
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit,tmdbResponse:(request,_)=>{
            if(QueryHelpers.ParseQuery(request.RequestUri!.Query).GetValueOrDefault("append_to_response").ToString().Contains("release_dates"))
            {calls++;return Task.FromResult<HttpResponseMessage?>(Json(History(c.Candidates.First())));}
            return Task.FromResult<HttpResponseMessage?>(null);
        });
        var id=c.Candidates.First().Identity.Id;
        Assert.NotNull(await rig.Tmdb.GetMoviePriorityEvidenceAsync(id,default));
        Assert.NotNull(await rig.Tmdb.GetMoviePriorityEvidenceAsync(id,default));
        Assert.Equal(1,calls);
    }

    private sealed class CaptureLogger:ILogger<PosterFetchService>
    {
        internal List<Dictionary<string,object?>> Events=new();
        public IDisposable? BeginScope<TState>(TState state)where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>true;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? error,Func<TState,Exception?,string> format)
        {if(state is IEnumerable<KeyValuePair<string,object?>> values)Events.Add(values.ToDictionary(v=>v.Key,v=>v.Value));}
    }
}
