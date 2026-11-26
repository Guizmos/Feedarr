using System.Text.Json;
using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Tests;

public sealed class TelevisionMatchingP5Tests
{
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static readonly SearchIdentity Id = new("tmdb", SearchMediaType.Series, "1");
    private static CandidateRankingContext Context(string raw) {
        var parsed = TelevisionSearchInput.Parse(raw);
        return new(SearchCandidateGenerator.Generate(new(raw, UnifiedCategory.Serie)), UnifiedCategory.Serie,
            SearchMediaType.Series, Season: parsed.Season, Episode: parsed.Episode, AirDate: parsed.AirDate);
    }

    [Theory]
    [InlineData("Affaires.sensibles.S06E03.Mathieu.Haulbert.2026.10.31.FRENCH.1080p.WEB.H264",6,3,"2026-10-31")]
    [InlineData("66.minutes.S2026E13.2026.11.22.FRENCH.1080p.WEB.H264",null,null,"2026-11-22")]
    [InlineData("66.minutes.grand.format.S2026E13.2026.11.22.FRENCH.1080p.WEB.H264",null,null,"2026-11-22")]
    public void EpisodeDatesAndCalendarSeasonsHaveTheSameInterpretationAcrossSources(string raw,int? season,int? episode,string date)
    {
        var parsed=TelevisionSearchInput.Parse(raw);
        Assert.Equal(season,parsed.Season); Assert.Equal(episode,parsed.Episode); Assert.Equal(date,parsed.AirDate);
        var rss=SearchCandidateGenerator.Generate(new(raw,UnifiedCategory.Emission));
        var torznab=SearchCandidateGenerator.Generate(new(raw,UnifiedCategory.Serie));
        Assert.Equal(rss,torznab); Assert.All(rss,q=>Assert.Null(q.Year));
        var row=new ProviderSearchCandidate(Id,rss[0].Title,2001,Season:season,Episode:episode,
            Television:new(Id,season.HasValue?true:null,episode.HasValue?true:null,date));
        Assert.Equal(CandidateDecision.Accept,SearchCandidateRanker.Rank(Context(raw),[row]).Decision);
    }

    [Fact]
    public void AnnualNumberWithoutConcordantDateNeverBecomesAnOrdinalSeason()
    {
        foreach(var raw in new[]{"66.minutes.S2026E13.1080p.WEB.H264","66.minutes.S2026E13.2025.11.22.1080p.WEB.H264"})
        {
            var context=Context(raw); Assert.Equal(2026,context.Season);
            var row=new ProviderSearchCandidate(Id,"66 minutes",2006,Season:20,Episode:13,
                Television:new(Id,true,true,"2025-11-22"));
            Assert.NotEqual(CandidateDecision.Accept,SearchCandidateRanker.Rank(context,[row]).Decision);
        }
    }

    [Theory]
    [InlineData("Programme.S36.2026.FRENCH.720p.HDTV.x265")]
    [InlineData("Programme.2026.FRENCH.720p.HDTV.x265")]
    public void ASeasonOrBroadcastYearCannotRejectAnEarlierSeriesPremiere(string raw)
    {
        var context=Context(raw);
        Assert.All(context.Queries.Where(q=>q.Year.HasValue),q=>Assert.Equal(CandidateYearEvidence.Hypothesis,q.YearEvidence));
        var row=new ProviderSearchCandidate(Id,"Programme",1990,Season:context.Season,
            Television:new(Id,context.Season.HasValue?true:null,null));
        var result=SearchCandidateRanker.Rank(context,[row]);
        Assert.DoesNotContain(result.Candidates.SelectMany(c=>c.Contradictions),c=>c.Code=="parsed-year-mismatch");
        if(context.Season.HasValue) Assert.Equal(CandidateDecision.Accept,result.Decision);
        else Assert.NotEqual(CandidateDecision.Accept,result.Decision); // A broadcast year alone proves no episode.
    }

    [Fact]
    public void ProgrammeSubjectProposalNeedsIndependentEpisodeProof()
    {
        const string raw="Affaires.sensibles.-.Mathieu.Haulbert.lenfant.disparu.du.Verdon.2026.VOF.1080p.HDTV.H264";
        var queries=SearchCandidateGenerator.Generate(new(raw,UnifiedCategory.Emission));
        Assert.Equal(2,queries.Count); Assert.Contains("Mathieu",queries[0].Title);
        Assert.Equal("Affaires sensibles",queries[1].Title); Assert.Equal(SearchCandidateOrigin.ProgrammeTitleHypothesis,queries[1].Origin);
        var row=new ProviderSearchCandidate(Id,"Affaires sensibles",2026,Television:new(Id,null,null));
        var result=SearchCandidateRanker.Rank(new(queries,UnifiedCategory.Emission,SearchMediaType.Series),[row]);
        Assert.NotEqual(CandidateDecision.Accept,result.Decision);
        Assert.Contains(result.Candidates.SelectMany(c=>c.Contradictions),c=>c.Code=="programme-subject-needs-episode-evidence");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntegralChannelSuffixMustBeVerifiedOnTheCandidate(bool verified)
    {
        const string raw="Esprit.Japon.Nolife.INTEGRALE.FRENCH.720p.WEB.x264.AAC-AnimesForAll";
        var context=Context(raw); Assert.Equal(2,context.Queries.Count);
        Assert.Equal("Nolife",context.Queries[1].ScopeSuffix);
        var rows=await TelevisionCandidates.EnrichAsync(context.Queries,UnifiedCategory.Emission,raw,
            [new(Id,"Esprit Japon",2014)],(_,_,_)=>Task.FromResult<JsonElement?>(Json(verified?
                "{\"id\":1,\"name\":\"Esprit Japon\",\"first_air_date\":\"2014-01-01\",\"networks\":[{\"name\":\"Nolife\"}],\"seasons\":[]}":
                "{\"id\":1,\"name\":\"Esprit Japon\",\"first_air_date\":\"2014-01-01\",\"networks\":[{\"name\":\"Other\"}],\"seasons\":[]}")),false,default);
        if (verified) Assert.Equal(CandidateDecision.Accept,SearchCandidateRanker.Rank(context,rows).Decision);
        else Assert.NotEqual(CandidateDecision.Accept,SearchCandidateRanker.Rank(context,rows).Decision);
        if(verified)
        {
            var competitor=rows[0] with {Identity=new("tmdb",SearchMediaType.Series,"2"),Television=rows[0].Television! with {Identity=new("tmdb",SearchMediaType.Series,"2")}};
            Assert.Equal(CandidateDecision.Ambiguous,SearchCandidateRanker.Rank(context,[rows[0],competitor]).Decision);
        }
    }

    [Theory]
    [InlineData("Traces.S01E02.VFF.1080p.WEB.H264")]
    [InlineData("Lie.To.Me.S02.VO.ST.ENGLISH.1080p.WEBRip.x265")]
    [InlineData("Mauvaise.Influence.S01E06.VFQ.720p.WEB.H264")]
    [InlineData("C.Est.La.Famille.S08E02.FRENCH.1080p.WEB.H264")]
    public async Task OfficialOriginalTitlesAreUsableButDoNotResolveGenuineHomonyms(string raw)
    {
        var context=Context(raw); var expected=context.Queries[0].Title;
        var details=Json(JsonSerializer.Serialize(new { id=1,name="Localized foreign name",original_name=expected,first_air_date="2000-01-01",seasons=new[]{new{season_number=context.Season,episode_count=20}} }));
        var episode=Json(JsonSerializer.Serialize(new {id=101,show_id=1,season_number=context.Season,episode_number=context.Episode,air_date="2020-01-01"}));
        var rows=await TelevisionCandidates.EnrichAsync(context.Queries,UnifiedCategory.Serie,raw,[new(Id,"Localized foreign name",2000)],
            (_,path,_)=>Task.FromResult<JsonElement?>(path.Contains("/season/")?episode:details),false,default);
        Assert.Equal(CandidateDecision.Accept,SearchCandidateRanker.Rank(context,rows).Decision);
        var other=rows[0] with {Identity=new("tmdb",SearchMediaType.Series,"2"),OfficialTitles=null,Title=expected,
            Television=rows[0].Television! with{Identity=new("tmdb",SearchMediaType.Series,"2")}};
        Assert.Equal(CandidateDecision.Ambiguous,SearchCandidateRanker.Rank(context,[rows[0],other]).Decision);
    }

    [Fact]
    public async Task TvmazeOfficialAliasesAreLoadedOnlyWhenNamesDoNotAlreadyMatch()
    {
        var context=Context("Programme.S02.1080p.WEB.H264");
        var paths=new List<string>();
        var rows=await TelevisionCandidates.EnrichAsync(context.Queries,UnifiedCategory.Serie,"Programme.S02.1080p.WEB.H264",[],
            (_,path,_)=>{
                paths.Add(path);
                return Task.FromResult<JsonElement?>(Json(path.StartsWith("search/")?
                    "[{\"show\":{\"id\":1,\"name\":\"Foreign title\",\"premiered\":\"2000-01-01\"}}]":
                    path.EndsWith("/akas")?"[{\"name\":\"Programme\"}]":
                    path.EndsWith("/episodes")?"[{\"id\":10,\"season\":2,\"number\":1,\"airdate\":\"2001-01-01\",\"_links\":{\"show\":{\"href\":\"https://api.tvmaze.com/shows/1\"}}}]":
                    "{\"id\":1,\"name\":\"Foreign title\",\"premiered\":\"2000-01-01\"}"));
            },true,default);
        Assert.Contains("shows/1/akas",paths);
        Assert.Equal(CandidateDecision.Accept,SearchCandidateRanker.Rank(context,rows).Decision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossProviderMergeKeepsOfficialAliasesAndRejectsContradictoryExternalIds(bool conflict)
    {
        const string raw="Programme.S02.1080p.WEB.H264"; var context=Context(raw);
        Task<JsonElement?> Get(string provider,string path,CancellationToken _)
        {
            var value=provider=="tmdb"?
                "{\"id\":1,\"name\":\"Foreign\",\"first_air_date\":\"2000-01-01\",\"seasons\":[{\"season_number\":2,\"episode_count\":10}],\"external_ids\":{\"tvdb_id\":77,\"imdb_id\":\"tt1234567\"}}":
                path.StartsWith("search/")?"[{\"show\":{\"id\":2,\"name\":\"Foreign\",\"premiered\":\"2000-01-01\"}}]":
                path.EndsWith("/akas")?"[{\"name\":\"Programme\"}]":
                path.EndsWith("/episodes")?"[{\"id\":20,\"season\":2,\"number\":1,\"airdate\":\"2001-01-01\",\"_links\":{\"show\":{\"href\":\"https://api.tvmaze.com/shows/2\"}}}]":
                JsonSerializer.Serialize(new{id=2,name="Foreign",premiered="2000-01-01",externals=new{thetvdb=77,imdb=conflict?"tt7654321":"tt1234567"}});
            return Task.FromResult<JsonElement?>(Json(value));
        }
        if(conflict)
        {
            var error=await Assert.ThrowsAsync<InvalidDataException>(()=>TelevisionCandidates.EnrichAsync(context.Queries,UnifiedCategory.Serie,raw,[new(Id,"Foreign",2000)],Get,true,default));
            Assert.Equal("tv_external_identity_conflict",error.Message);
        }
        else
        {
            var rows=await TelevisionCandidates.EnrichAsync(context.Queries,UnifiedCategory.Serie,raw,[new(Id,"Foreign",2000)],Get,true,default);
            Assert.Single(rows); Assert.Equal(Id,rows[0].Identity);
            Assert.Equal(CandidateDecision.Accept,SearchCandidateRanker.Rank(context,rows).Decision);
            Assert.Contains(rows[0].OfficialTitles!,t=>t.Title=="Programme"&&t.Identity==Id);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CalendarSeasonUsesVerifiedEpisodeDateAndExternalLinkWithoutInventingOrdinalCoordinates(bool foreignEpisode)
    {
        const string raw="Programme.S2026E13.2026.11.22.FRENCH.1080p.WEB.H264";
        var context=Context(raw);var paths=new List<string>();
        var rows=await TelevisionCandidates.EnrichAsync(context.Queries,UnifiedCategory.Emission,raw,[new(Id,"Programme",2000)],
            (provider,path,_)=>{
                paths.Add(path);
                var value=provider=="tmdb"?"{\"id\":1,\"name\":\"Programme\",\"first_air_date\":\"2000-01-01\",\"seasons\":[],\"external_ids\":{\"tvdb_id\":77}}":
                    path.StartsWith("search/")?"[{\"show\":{\"id\":2,\"name\":\"Programme\",\"premiered\":\"2000-01-01\"}}]":
                    path.Contains("episodesbydate")?JsonSerializer.Serialize(new[]{new{id=20,season=36,number=13,airdate="2026-11-22",_links=new{show=new{href="https://api.tvmaze.com/shows/"+(foreignEpisode?"999":"2")}}}}):
                    "{\"id\":2,\"name\":\"Programme\",\"premiered\":\"2000-01-01\",\"externals\":{\"thetvdb\":77}}";
                return Task.FromResult<JsonElement?>(Json(value));
            },true,default);
        Assert.DoesNotContain(paths,p=>p.Contains("season/2026"));
        Assert.Contains("shows/2/episodesbydate?date=2026-11-22",paths);
        var result=SearchCandidateRanker.Rank(context,rows);
        if(foreignEpisode) Assert.NotEqual(CandidateDecision.Accept,result.Decision);
        else {Assert.Equal(Id,result.SelectedIdentity);Assert.Equal(CandidateDecision.Accept,result.Decision);}
        Assert.Null(rows[0].Season);Assert.Null(rows[0].Episode); // No calendar-year-to-provider-ordinal mapping.
    }

    [Fact]
    public async Task MissingAliasCoverageCannotSilentlyRemoveACompetitor()
    {
        var context=Context("Programme.S02.1080p.WEB.H264");
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>TelevisionCandidates.EnrichAsync(context.Queries,UnifiedCategory.Serie,"Programme.S02.1080p.WEB.H264",[],
            (_,path,_)=>Task.FromResult<JsonElement?>(path.StartsWith("search/")?Json("[{\"show\":{\"id\":1,\"name\":\"Foreign\",\"premiered\":\"2000-01-01\"}}]"):
                path.EndsWith("/akas")?null:Json("{\"id\":1,\"name\":\"Foreign\",\"premiered\":\"2000-01-01\"}")),true,default));
        Assert.Equal("tvmaze_alias_coverage_unavailable",error.Message);
        Assert.True(VideoMatchingFailure.KnownInvariant(error.Message));
    }

    [Fact]
    public async Task P4ReuseRefusalRemainsSeparateFromTvMatchingDiagnostics()
    {
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeHit,completeTmdbMetadata:true);
        var id=rig.IngestRelease("Show.Tvmaze.Hit.S01E02.1080p.WEB.x264-GRP",UnifiedCategory.Serie);
        using var diagnostics=MatchingAttemptDiagnostics.Begin(id);
        Assert.True((await rig.FetchAsync(id)).Ok);
        Assert.Contains("local_identity_reuse",diagnostics.Coverage);
        Assert.Contains("no_candidate",diagnostics.Coverage);
        Assert.Contains("tv_input",diagnostics.Coverage);
        Assert.Contains("tv_evidence",diagnostics.Coverage);
    }

    [Theory]
    [InlineData(UnifiedCategory.Film,"Sacrifice.S02E10.FRENCH.1080p.WEB.H264")]
    [InlineData(UnifiedCategory.Animation,"Albator84.1982.S01.COMPLETE.VFF.DVDRiP.AVC-NoTag.FRENCH")]
    public void StrongEpisodeEvidenceOverridesFunctionalMovieCategoryOnlyForUnboundIdentity(UnifiedCategory category,string raw)
    {
        var row=new ReleaseForPoster{Title=raw,CategoryIds="2000",MediaType="movie"};
        Assert.Equal("tv",PosterFetchService.ResolveVideoType(row,category));
        row.IsDirectRss=true; Assert.Equal("tv",PosterFetchService.ResolveVideoType(row,category));
        row.TmdbMediaType="movie"; Assert.NotEqual("tv",PosterFetchService.ResolveVideoType(row,category));
        row.TmdbMediaType=null;row.CategoryIds="2000,5000";Assert.Null(PosterFetchService.ResolveVideoType(row,category));
    }

    [Theory]
    [InlineData(UnifiedCategory.Film)]
    [InlineData(UnifiedCategory.Animation)]
    public async Task PersistedEpisodesRouteToTvWithoutChangingCategoryOrReimport(UnifiedCategory category)
    {
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeHit,completeTmdbMetadata:true);
        var id=rig.IngestRelease("Show.Tvmaze.Hit.S01E02.1080p.WEB.x264-GRP",category);
        using(var db=rig.Database.Open())db.Execute("UPDATE releases SET title_clean='obsolete',season=NULL,episode=NULL,poster_last_error='old' WHERE id=@id",new{id});
        Assert.True((await rig.FetchAsync(id)).Ok);
        var row=rig.Repository.GetForPoster(id)!;Assert.Equal(category.ToString(),row.UnifiedCategory);Assert.Equal("tvmaze",row.ExtProvider);
        Assert.All(rig.TmdbSearchRequests,request=>Assert.Contains("/search/tv",request.AbsolutePath));
    }

    [Fact]
    public async Task GenericTitlesKeepBothCandidateAndEnrichmentBudgets()
    {
        var context=Context("Survivor.S13.Cook.Islands.VOSTFR.540p.WEBRip.x264");
        var collection=await VideoSearchCollector.CollectAsync(["Survivor"],["fr-FR","en-US"],
            (_,_,page,_)=>Task.FromResult<TmdbClient.VideoMatchingPage?>(new(
                Enumerable.Range((page-1)*20+1,Math.Min(20,105-(page-1)*20)).Select(i=>new TmdbClient.SearchResult(i,"Survivor",null,null,"tv",2000,null)).ToArray(),6,105)),default);
        Assert.True(collection.Complete); Assert.Equal(12,collection.Counters.SearchRequests);
        var projected=TmdbShadowCandidateAdapter.Project(collection.Observed,context);
        Assert.Equal(105,projected.Length);
        Assert.Contains("provider-result-limit-exceeded",SearchCandidateRanker.Rank(context,projected).Reasons);
        var queries=SearchCandidateGenerator.Generate(new("Fort.Boyard.S36.FRENCH.720p.HDTV.x265",UnifiedCategory.Emission));
        var candidates=Enumerable.Range(1,7).Select(i=>new ProviderSearchCandidate(new("tmdb",SearchMediaType.Series,i.ToString()),"Fort Boyard",1990)).ToArray();
        var diagnostics=new List<string>();var calls=0;
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>TelevisionCandidates.EnrichAsync(queries,UnifiedCategory.Emission,"Fort.Boyard.S36.FRENCH.720p.HDTV.x265",candidates,
            (_,_,_)=>{calls++;return Task.FromResult<JsonElement?>(null);},false,default,diagnostics.Add));
        Assert.Equal("tv_evidence_budget",error.Message);Assert.Equal(0,calls);Assert.Contains("plausible=7",diagnostics.Last());Assert.Contains("limit=6",diagnostics.Last());
    }

    [Fact]
    public void DatedGenericCollectionCanPruneOnlyImpossibleFuturePremieresBeforeTheBudget()
    {
        var context=Context("Programme.S02E03.2026.11.22.1080p.HDTV.H264");
        var observations=Enumerable.Range(1,105).Select(i=>new TmdbClient.ObservedSearchResult(new(i,"Programme",null,null,"tv",i<=102?2028:i==103?null:2000,null),"fr-FR")).ToArray();
        var rows=TmdbShadowCandidateAdapter.Project(observations,context);
        Assert.Equal(3,rows.Length); Assert.Contains(rows,r=>r.Year is null);
        Assert.Equal(105,TmdbShadowCandidateAdapter.Project(observations,context with{AirDate=null}).Length);
        Assert.Equal(105,TmdbShadowCandidateAdapter.Project(observations,context with{HasManualCorrection=true}).Length);
        Assert.Equal(105,TmdbShadowCandidateAdapter.Project(observations,context with{RssEvidence=new(1,1,null,null,null)}).Length);
    }

    [Fact]
    public void ConflictingPremiereRecordsStayVisibleAndChronologyCannotBeOverriddenByAnAlias()
    {
        var context=Context("Programme.S02E03.2026.11.22.1080p.HDTV.H264");
        var candidate=new ProviderSearchCandidate(Id,"Programme",2028,Television:new(Id,null,null,"2026-11-22"));
        var rows=SearchCandidateRanker.PruneTelevisionPremiereContradictions(context,[candidate,candidate with{Year=2025}]);
        Assert.Equal(2,rows.Length);
        var result=SearchCandidateRanker.Rank(context,[candidate]);
        Assert.Equal(CandidateDecision.Reject,result.Decision);
        Assert.Contains(result.Candidates.Single().Contradictions,c=>c.Code=="series-premiere-after-episode-date");
    }

    [Fact]
    public async Task IdenticalTvSearchesAcrossSourcesUseExistingResponseCaches()
    {
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeHit,completeTmdbMetadata:true);
        await Task.WhenAll(Enumerable.Range(0,4).Select(_=>rig.Tmdb.SearchVideoMatchingPageAsync("Show Tvmaze Hit","fr-FR",1,"tv",default)));
        Assert.Single(rig.TmdbSearchRequests);
        await Task.WhenAll(Enumerable.Range(0,4).Select(_=>rig.Tvmaze.GetMatchingEvidenceAsync("search/shows?q=Show%20Tvmaze%20Hit",default)));
        Assert.Equal(1,rig.TvmazeMatchingRequests);
    }
}
