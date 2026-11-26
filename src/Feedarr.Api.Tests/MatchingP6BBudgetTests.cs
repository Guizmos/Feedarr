using System.Net;
using System.Text.Json;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Tmdb;
using Microsoft.AspNetCore.WebUtilities;
using Xunit.Abstractions;

namespace Feedarr.Api.Tests;

public sealed class MatchingP6BBudgetTests(ITestOutputHelper output)
{
    private sealed record Metrics(int Logical,int Transports,int Enriched,string Outcome);
    private sealed class Fixture : IDisposable
    {
        internal readonly PosterMatchingContractTestRig Rig;
        internal readonly CandidateRankingContext Context;
        internal readonly string Raw;
        internal int Logical,Transports,Enriched,Limit;
        internal readonly int Total,Strong;
        internal bool CorruptLast,LastStrong,PartialDetails;
        internal int RescueAliasId;
        internal readonly bool Movie,Wide;
        internal readonly string Title;
        internal Fixture(string raw,int total,int strong=1,bool movie=false,bool wide=false)
        {
            Raw=raw;Total=total;Strong=strong;Movie=movie;Wide=wide;
            var parsed=TelevisionSearchInput.Parse(raw);
            var category=movie?UnifiedCategory.Film:UnifiedCategory.Serie;
            var queries=SearchCandidateGenerator.Generate(new(raw,category));
            Context=new(queries,category,movie?SearchMediaType.Movie:SearchMediaType.Series,
                Season:movie?null:parsed.Season,Episode:movie?null:parsed.Episode,AirDate:movie?null:parsed.AirDate);
            Title=queries.Last().Title;
            Rig=new(PosterContractScenario.SeriesTvmazeMissTmdb,tmdbResponse:Respond,
                rawgResponse:(_,_)=>throw new InvalidOperationException("RAWG forbidden in P6B"));
        }
        private Task<HttpResponseMessage?> Respond(HttpRequestMessage request,CancellationToken _)
        {
            if(request.RequestUri!.Host!="api.themoviedb.org")return Task.FromResult<HttpResponseMessage?>(null);
            Transports++;var path=request.RequestUri.AbsolutePath;var query=QueryHelpers.ParseQuery(request.RequestUri.Query);
            object payload;
            if(path.Contains("/search/"))
            {
                var page=int.Parse(query["page"]!);var title=query["query"].ToString();
                var requestedYear=query.TryGetValue("primary_release_year",out var value)?int.Parse(value!):(int?)null;
                var count=Movie&&requestedYear!=2022||Wide&&title!=Title?0:Total;
                var rows=count==0?Array.Empty<object>():Enumerable.Range((page-1)*20+1,Math.Min(20,count-(page-1)*20)).Select(i=>(object)new{
                    id=i,name=i<=Strong||LastStrong&&i==Total?Title:"Lexically distant competitor "+i,
                    title=i<=Strong||LastStrong&&i==Total?Title:"Lexically distant competitor "+i,
                    original_name=i<=Strong||LastStrong&&i==Total?Title:"Unrelated name "+i,
                    original_title=i<=Strong||LastStrong&&i==Total?Title:"Unrelated name "+i,
                    first_air_date="2000-01-01",release_date="2022-01-01",poster_path="/fr.jpg"}).ToArray();
                if(CorruptLast&&page==(int)Math.Ceiling(count/20d)&&rows.Length>0)rows=rows[..^1];
                payload=new{page,total_pages=(int)Math.Ceiling(count/20d),total_results=count,results=rows};
            }
            else
            {
                var parts=path.Split('/');var id=int.Parse(parts[3]);
                if(path.Contains("/season/")) payload=new{id=id*1000,show_id=id,season_number=Context.Season,episode_number=Context.Episode,air_date=Context.AirDate??"2026-11-22"};
                else if(path.EndsWith("/credits"))payload=new{id,cast=Array.Empty<object>(),crew=Array.Empty<object>()};
                else if(PartialDetails)payload=new{id,name=Title,first_air_date="2000-01-01",seasons=new[]{new{season_number=Context.Season,episode_count=30}},external_ids=new{},alternative_titles=new{results=Array.Empty<object>()}};
                else payload=new{id,name=id<=Strong||LastStrong&&id==Total||id==RescueAliasId?Title:"Unrelated name "+id,original_name=id<=Strong||LastStrong&&id==Total||id==RescueAliasId?Title:"Unrelated name "+id,
                    first_air_date="2000-01-01",seasons=new[]{new{season_number=Context.Season,episode_count=30}},external_ids=new{},alternative_titles=new{results=Array.Empty<object>()},
                    overview=query["language"].ToString()=="en-US"?"English synopsis":"Résumé français",tagline="",genres=Array.Empty<object>(),episode_run_time=new[]{45},vote_average=7,vote_count=10,poster_path="/fr.jpg"};
            }
            return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(payload))});
        }
        internal Task<TmdbClient.VideoMatchingPage?> Page(string title,string language,int page,int? year,CancellationToken ct)
        {Logical++;return Rig.Tmdb.SearchVideoMatchingPageAsync(title,language,page,Movie?"movie":"tv",ct,year);}
        internal async Task<Metrics> Run(bool legacy)
        {
            var startL=Logical;var startT=Transports;Enriched=0;
            VideoSearchCollection collection;
            if(legacy)
                collection=Movie?await VideoSearchCollector.CollectYearsAsync(Context.Queries.Select(q=>q.Title),["fr-FR","en-US"],[2022,2021,2023],Page,default):
                    await VideoSearchCollector.CollectAsync(Context.Queries.Select(q=>q.Title),["fr-FR","en-US"],(t,l,p,ct)=>Page(t,l,p,null,ct),default);
            else collection=await MovieYearSearchCollector.CollectAsync(Context,["fr-FR","en-US"],Page,default);
            if(!collection.Complete)return new(Logical-startL,Transports-startT,0,"search_budget_or_coverage");
            var rows=TmdbShadowCandidateAdapter.Project(collection.Observed,Context);
            var limit=legacy||Movie?100:TelevisionCandidates.ProjectionLimit(Context,rows);
            if(rows.Length>limit)return new(Logical-startL,Transports-startT,0,"projection_budget");
            if(legacy)
            {
                // Frozen P5 gate from the audited implementation. All these same-type
                // rows could gain an unknown alias; it required every row, then stopped at six.
                if(rows.Length>6)return new(Logical-startL,Transports-startT,0,"evidence_budget");
                foreach(var row in rows)
                {
                    Logical++;await Rig.Tmdb.GetTelevisionEvidenceAsync("tv/"+row.Identity.Id+"?append_to_response=external_ids,alternative_titles",default);
                    if(Context.Season.HasValue&&Context.Episode.HasValue)
                    {Logical++;await Rig.Tmdb.GetTelevisionEvidenceAsync($"tv/{row.Identity.Id}/season/{Context.Season}/episode/{Context.Episode}",default);}
                }
                return new(Logical-startL,Transports-startT,rows.Length,"baseline_evaluated");
            }
            Limit=limit==150?40:30;
            var used=collection.Counters.SearchRequests;
            rows=await TelevisionCandidates.EnrichAsync(Context.Queries,Context.Category,Raw,rows,async(provider,path,ct)=>{
                Assert.Equal("tmdb",provider);if(used>=Limit)throw new InvalidDataException("tv_http_budget");
                used++;Logical++;return await Rig.Tmdb.GetTelevisionEvidenceAsync(path,ct);
            },false,default,d=>{
                if(d.StartsWith("detailsValidated="))Enriched=int.Parse(d.Split(';')[1].Split('=')[1]);
            },Context,n=>Limit=n);
            var ranked=SearchCandidateRanker.Rank(Context,rows,new CandidateRankingPolicy(MaximumResults:limit));
            Assert.Empty(Rig.RawgSearchRequests);
            return new(Logical-startL,Transports-startT,Enriched,ranked.Decision.ToString().ToLowerInvariant());
        }
        public void Dispose()=>Rig.Dispose();
    }

    public static IEnumerable<object[]> References()
    {
        yield return ["Survivor","Survivor.S13.Cook.Islands.540p.WEBRip.x264",105];
        yield return ["Fort Boyard","Fort.Boyard.S36.FRENCH.720p.HDTV.x265",24];
        yield return ["Traces","Traces.S01E02.VFF.1080p.WEB.H264",37];
        yield return ["Sacrifice","Sacrifice.S02E10.FRENCH.1080p.WEB.H264",26];
        yield return ["Lie To Me","Lie.To.Me.S02.VO.ST.ENGLISH.1080p.WEBRip.x265",9];
        yield return ["Bel bien","Bel.bien.-.Pourquoi.nous.ne.savons.plus.nous.reposer.2026.VOF.1080p.HDTV.H264",11];
        yield return ["Sugar Rush","Sugar.Rush.S02.1080p.WEB.H264",10];
        yield return ["L'Amour est dans le pré","L.Amour.est.dans.le.pre.S21E19.2026.11.30.1080p.HDTV.H264",8];
    }
    [Theory][MemberData(nameof(References))]
    public async Task ReferenceCostComparisonUsesFrozenDiagnosticSizesNotInventedRealGains(string name,string raw,int total)
    {
        using var before=new Fixture(raw,total);var old=await before.Run(true);
        using var after=new Fixture(raw,total);var current=await after.Run(false);
        Assert.Equal(1,current.Enriched);Assert.NotEqual("evidence_budget",current.Outcome);Assert.InRange(current.Logical,1,40);
        var warm=await after.Run(false);Assert.Equal(0,warm.Transports);Assert.Equal(current.Logical,warm.Logical);
        output.WriteLine($"P6B_CASE {name} | before L={old.Logical} T={old.Transports} E={old.Enriched} {old.Outcome} | after L={current.Logical} T={current.Transports} E={current.Enriched} {current.Outcome} | warm L={warm.Logical} T={warm.Transports} E={warm.Enriched}");
    }

    [Theory][InlineData("The One")][InlineData("'Oro")][InlineData("Enquete")]
    public async Task PromisingTypedPlansCanFinishEighteenPagesWithoutRestartingTheirFirstPages(string name)
    {
        var movie=name=="The One";var raw=movie?"The.One.2022.1080p.WEB.H264":name+".2026.S01E01.1080p.HDTV.H264";
        using var before=new Fixture(raw,movie?121:141,movie:movie,wide:!movie);var old=await before.Run(true);
        using var after=new Fixture(raw,movie?121:141,movie:movie,wide:!movie);var current=await after.Run(false);
        Assert.Equal("search_budget_or_coverage",old.Outcome);Assert.Equal(18,movie?current.Logical:current.Logical-2);
        Assert.Equal(movie?"projection_budget":"accept",current.Outcome);
        output.WriteLine($"P6B_CASE {name} | before L={old.Logical} T={old.Transports} E=0 {old.Outcome} | after L={current.Logical} T={current.Transports} E={current.Enriched} {current.Outcome}");
    }

    [Fact]
    public async Task SmallPoolsDoNotSpendTheExtendedBudgetOrEnrichAnIrrelevantTail()
    {
        using var before=new Fixture("Example.S01E01.1080p.WEB.H264",6);var old=await before.Run(true);
        using var after=new Fixture("Example.S01E01.1080p.WEB.H264",6);var current=await after.Run(false);
        Assert.Equal(14,old.Logical);Assert.Equal(14,old.Transports);Assert.Equal(6,old.Enriched);
        Assert.Equal(4,current.Logical);Assert.Equal(4,current.Transports);Assert.Equal(1,current.Enriched);
        output.WriteLine($"P6B_CASE small_pool | before L={old.Logical} T={old.Transports} E={old.Enriched} | after L={current.Logical} T={current.Transports} E={current.Enriched}");
    }

    [Theory][InlineData(1,true)][InlineData(4,false)]
    public async Task UnknownAliasesUseAtMostTwoFallbackCandidates(int rescue,bool accepts)
    {
        using var fixture=new Fixture("Example.S01E01.1080p.WEB.H264",4,0){RescueAliasId=rescue};
        var result=await fixture.Run(false);Assert.Equal(2,result.Enriched);Assert.Equal(6,result.Logical);
        Assert.Equal(accepts,result.Outcome=="accept"); // Accepted residual risk: alias in the unexpanded tail.
    }

    [Fact]
    public async Task AllThirteenStrongCompetitorsCauseAbstentionBeforeAnyAreTruncated()
    {
        using var fixture=new Fixture("Example.S01E01.1080p.WEB.H264",13,13);
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>fixture.Run(false));
        Assert.Equal("tv_evidence_budget",error.Message);Assert.Equal(2,fixture.Logical);Assert.Equal(2,fixture.Transports);
    }

    [Fact]
    public async Task TwelveStrongCompetitorsAreAllEnrichedAndRemainAmbiguous()
    {
        using var fixture=new Fixture("Example.S01E01.1080p.WEB.H264",12,12);
        var result=await fixture.Run(false);Assert.Equal(12,result.Enriched);Assert.Equal(26,result.Logical);Assert.Equal("ambiguous",result.Outcome);
    }

    [Fact]
    public async Task AStrongCompetitorOnTheLastPageIsNeverHidden()
    {
        using var fixture=new Fixture("Example.S01E01.1080p.WEB.H264",105){LastStrong=true};
        var result=await fixture.Run(false);Assert.Equal(2,result.Enriched);Assert.Equal("ambiguous",result.Outcome);
    }

    [Fact]
    public async Task MalformedLastPageNeverExposesPartialCandidates()
    {
        using var fixture=new Fixture("Example.S01E01.1080p.WEB.H264",141,wide:false){CorruptLast=true};
        var result=await fixture.Run(false);Assert.Equal("search_budget_or_coverage",result.Outcome);Assert.Equal(0,result.Enriched);
    }

    [Fact]
    public async Task FortyLogicalRequestsAreAllowedButTheFortyFirstIsNeverTransported()
    {
        using(var exact=new Fixture("Example.S01E01.1080p.WEB.H264",141,12))
        {
            var result=await exact.Run(false);Assert.Equal(40,result.Logical);Assert.Equal(40,result.Transports);Assert.Equal(12,result.Enriched);
        }
        using var exceeds=new Fixture("Example.2026.S01E01.1080p.WEB.H264",141,12,wide:true);
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>exceeds.Run(false));
        Assert.Equal("tv_http_budget",error.Message);Assert.Equal(40,exceeds.Logical);Assert.Equal(40,exceeds.Transports);Assert.Equal(10,exceeds.Enriched);
    }

    [Fact]
    public async Task PublicationCannotReuseAnEnglishSnapshotForFrenchOrBypassExplicitRefresh()
    {
        using var fixture=new Fixture("Example.S01.1080p.WEB.H264",1);
        fixture.Rig.Settings.SaveUi(new(){MediaInfoLanguage="en-US"});
        await fixture.Rig.Tmdb.GetTelevisionEvidenceAsync("tv/1?append_to_response=external_ids,alternative_titles",default);
        fixture.Rig.Settings.SaveUi(new(){MediaInfoLanguage="fr-FR"});
        var before=fixture.Transports;
        var details=await fixture.Rig.Tmdb.GetTvPublicationDetailsAsync(1,default);
        Assert.Equal(before+2,fixture.Transports);Assert.Equal("Résumé français",details!.Overview);
        before=fixture.Transports;
        await fixture.Rig.Tmdb.GetTvPublicationDetailsAsync(1,default,true);
        Assert.Equal(before+2,fixture.Transports);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task LocalizedEvidenceIsReusedOnlyWhenItsPublicationFieldsAreComplete(bool partial)
    {
        using var fixture=new Fixture("Example.S01.1080p.WEB.H264",1){PartialDetails=partial};
        await fixture.Rig.Tmdb.GetTelevisionEvidenceAsync("tv/1?append_to_response=external_ids,alternative_titles",default);
        var count=fixture.Transports;
        var details=await fixture.Rig.Tmdb.GetTvPublicationDetailsAsync(1,default);
        Assert.Equal(partial?count+2:count+1,fixture.Transports);Assert.NotNull(details);
        if(!partial)Assert.Equal("Résumé français",details!.Overview);
    }
}
