using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Posters;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace Feedarr.Api.Tests;

public sealed class MovieExactYearPriorityTests
{
    internal sealed record AuditCase(long ReleaseId, string RawTitle, string Title, int Year, string Category,
        string SourceKind, string SelectedId, ProviderSearchCandidate[] Candidates);
    internal static AuditCase[] Cases => JsonSerializer.Deserialize<AuditCase[]>(JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory,"Fixtures","Matching","exact-movie-year-audit.json"))).RootElement.GetProperty("Cases").GetRawText())!;
    private static AuditCase[] NegativeCases => JsonSerializer.Deserialize<AuditCase[]>(JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory,"Fixtures","Matching","exact-movie-year-audit.json"))).RootElement.GetProperty("NegativeCases").GetRawText())!;
    public static IEnumerable<object[]> NegativeIds => NegativeCases.Select(c=>new object[]{c.ReleaseId});
    public static IEnumerable<object[]> AuditIds => Cases.Select(c=>new object[]{c.ReleaseId});
    private static CandidateRankingContext Context(AuditCase c,UnifiedCategory? category=null) => new(
        SearchCandidateGenerator.Generate(new(c.RawTitle,category??Enum.Parse<UnifiedCategory>(c.Category))),
        category??Enum.Parse<UnifiedCategory>(c.Category),SearchMediaType.Movie);
    private static VideoSearchCollection Collection(int requests=6,bool complete=true) => new([],complete?null:"not_evaluable_search_budget",new(2,requests,requests,0),requests,"targeted_year");
    internal static JsonElement Aliases(ProviderSearchCandidate c) => JsonSerializer.SerializeToElement(new {
        id=int.Parse(c.Identity.Id),titles=(c.OfficialTitles??[]).Where(t=>t.Kind==ProviderTitleKind.Alternative).Select(t=>new{title=t.Title}).ToArray() });
    // Synthetic release-history additions model the guards; audit search data above is real.
    internal static JsonElement History(ProviderSearchCandidate c) => JsonSerializer.SerializeToElement(new {
        id=int.Parse(c.Identity.Id),title=c.Title,original_title=c.OfficialTitles!.First(t=>t.Kind==ProviderTitleKind.Original).Title,
        status="Released",release_date=$"{c.Year}-01-01",imdb_id=(string?)null,
        alternative_titles=Aliases(c),release_dates=new { results=new[]{new{iso_3166_1="US",release_dates=new[]{new{type=3,release_date=$"{c.Year}-01-01T00:00:00.000Z",note=""}}}} }
    });
    private static async Task<(CandidateRankingResult Result, MovieExactYearPriority Session, int ProofCalls)> Run(AuditCase c,
        Func<ProviderSearchCandidate,JsonElement?>? evidence=null,VideoSearchCollection? collection=null,
        CandidateRankingContext? context=null,ProviderSearchCandidate[]? candidates=null)
    {
        var ctx=context??Context(c);var pool=candidates??c.Candidates;var proofs=0;
        var session=new MovieExactYearPriority(ctx,c.RawTitle,collection??Collection(),pool,(id,_)=>{
            proofs++;return Task.FromResult(evidence is null?(JsonElement?)History(pool.First(p=>p.Identity.Id==id)):evidence(pool.First(p=>p.Identity.Id==id)));
        },(id,_)=>Task.FromResult<JsonElement?>(Aliases(pool.First(p=>p.Identity.Id==id))));
        var enriched=await MovieTitleCandidates.EnrichAsync(ctx,pool,session.LoadTitlesAsync,default);
        return (await session.ApplyAsync(enriched,SearchCandidateRanker.Rank(ctx,enriched),default),session,proofs);
    }
    private static AuditCase Case(long id=32718)=>Cases.Single(c=>c.ReleaseId==id);
    private static JsonElement Change(ProviderSearchCandidate c,Action<JsonObject> mutate)
    {var obj=JsonNode.Parse(History(c).GetRawText())!.AsObject();mutate(obj);return JsonSerializer.SerializeToElement(obj);}

    [Theory]
    [MemberData(nameof(AuditIds))]
    public async Task FourAuditedPairsGainPriorityOnlyWithCompleteVerifiedOriginalReleaseHistory(long id)
    {
        var c=Case(id);var baseline=SearchCandidateRanker.Rank(Context(c),c.Candidates);
        Assert.Equal(CandidateDecision.Ambiguous,baseline.Decision);Assert.Contains("close-competing-identities",baseline.Reasons);
        var tested=await Run(c);
        Assert.Equal(CandidateDecision.Accept,tested.Result.Decision);Assert.Equal(c.SelectedId,tested.Result.SelectedIdentity!.Id);
        Assert.Equal(MovieExactYearPriority.DecisionReason,Assert.Single(tested.Result.Reasons));
        Assert.Equal(1,tested.Result.Candidates.Single(x=>x.Identity.Id==c.SelectedId).Confidence);
        Assert.Equal("applied",tested.Session.Outcome);Assert.InRange(tested.Session.Requests,6,12);
        Assert.All(baseline.Candidates,prior=>Assert.Equal(prior.Confidence,tested.Result.Candidates.Single(x=>x.Identity==prior.Identity).Confidence));
        Assert.Equal(.08,new CandidateRankingPolicy().MinimumConfidenceGap);Assert.Equal(12,VideoSearchCollector.MaximumPages);
    }

    [Theory]
    [MemberData(nameof(NegativeIds))]
    public async Task AuditYearMismatchesRemainRejections(long id)
    {
        var c=NegativeCases.Single(c=>c.ReleaseId==id);var tested=await Run(c);
        Assert.Equal(CandidateDecision.Reject,tested.Result.Decision);Assert.Null(tested.Result.SelectedIdentity);
        Assert.Equal(0,tested.ProofCalls);
        Assert.Contains(tested.Result.Candidates.SelectMany(r=>r.Contradictions),x=>x.Code=="parsed-year-mismatch");
    }

    [Theory]
    [InlineData(UnifiedCategory.Film)] [InlineData(UnifiedCategory.Animation)]
    [InlineData(UnifiedCategory.Spectacle)] [InlineData(UnifiedCategory.Autre)]
    public async Task RuleDependsOnConfirmedMovieNotFunctionalCategory(UnifiedCategory category)
    {
        var c=Case();Assert.Equal(CandidateDecision.Accept,(await Run(c,context:Context(c,category))).Result.Decision);
    }

    [Theory]
    [InlineData("missing")] [InlineData("wrong_owner")] [InlineData("wrong_media")] [InlineData("wrong_title")] [InlineData("wrong_history_owner")] [InlineData("wrong_alias_owner")] [InlineData("wrong_primary_year")]
    [InlineData("earlier_original_date")] [InlineData("invalid_date")] [InlineData("pending_release")]
    public async Task ParsedYearCannotOverrideUnavailableOrContradictoryProviderEvidence(string mode)
    {
        var c=Case();
        var tested=await Run(c,evidence:p=>p.Identity.Id!=c.SelectedId?History(p):mode=="missing"?null:Change(p,obj=>{
            if(mode=="wrong_owner")obj["id"]=999;
            if(mode=="wrong_media")obj["media_type"]="tv";
            if(mode=="wrong_title")obj["original_title"]="Different work";
            if(mode=="wrong_history_owner")obj["release_dates"]!["id"]=999;
            if(mode=="wrong_alias_owner")obj["alternative_titles"]!["id"]=999;
            if(mode=="wrong_primary_year")obj["release_date"]=$"{c.Year-1}-01-01";
            if(mode=="pending_release")obj["status"]="Planned";
            if(mode=="earlier_original_date")obj["release_dates"]!["results"]![0]!["release_dates"]![0]!["release_date"]=$"{c.Year-1}-01-01T00:00:00Z";
            if(mode=="invalid_date")obj["release_dates"]!["results"]![0]!["release_dates"]![0]!["release_date"]="invalid";
        }));
        Assert.Equal(CandidateDecision.Ambiguous,tested.Result.Decision);Assert.Null(tested.Result.SelectedIdentity);
        Assert.NotEqual("applied",tested.Session.Outcome);Assert.InRange(tested.Session.Requests,6,12);
    }

    [Fact]
    public async Task SameYearHomonymsRemainAmbiguousWithoutFetchingOptionalProof()
    {
        var c=Case();var pool=c.Candidates.Select(p=>p.Identity.Id=="1634345"?p with {Year=c.Year}:p).ToArray();
        var tested=await Run(c,candidates:pool);
        Assert.Equal(CandidateDecision.Ambiguous,tested.Result.Decision);Assert.Equal(0,tested.ProofCalls);
        Assert.Equal("exact_year_identity_not_unique",tested.Session.Outcome);
    }

    [Theory]
    [InlineData("REMASTERED")] [InlineData("RM")] [InlineData("REMASTERISE")] [InlineData("REEDITION")] [InlineData("DIRECTORS.CUT")] [InlineData("EXTENDED.EDITION")]
    public async Task RawVideoEditionIsAnIdentityWarning(string marker)
    {
        var c=Case() with {RawTitle=$"Oppenheimer.2023.{marker}.1080p.WEB"};
        var tested=await Run(c,context:Context(Case()));
        Assert.Equal(CandidateDecision.Accept,tested.Result.Decision);
        Assert.Contains("video_release_edition",tested.Session.Warnings);
    }

    [Theory]
    [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public async Task SecondaryNeighborReleaseIsAWarning(int type)
    {
        var c=Case(32082);
        var tested=await Run(c,evidence:p=>p.Identity.Id==c.SelectedId?History(p):Change(p,obj=>{
            obj["release_dates"]!["results"]![0]!["release_dates"]!.AsArray().Add(new JsonObject { ["type"]=type,["release_date"]=$"{c.Year}-04-01T00:00:00Z",["note"]="Re-release" });
        }));
        Assert.Equal(CandidateDecision.Accept,tested.Result.Decision);
        Assert.Contains("secondary_release_in_requested_year",tested.Session.Warnings);
    }

    [Fact]
    public async Task RemasterNotesAndLaterRereleasesDoNotChangeOriginalIdentity()
    {
        var c=Case();
        var blocked=await Run(c,evidence:p=>p.Identity.Id==c.SelectedId?Change(p,obj=>obj["release_dates"]!["results"]![0]!["release_dates"]![0]!["note"]="4K remastered edition"):History(p));
        Assert.Equal(CandidateDecision.Accept,blocked.Result.Decision);
        Assert.Contains("video_edition_same_identity",blocked.Session.Warnings);
        var accepted=await Run(c,evidence:p=>p.Identity.Id==c.SelectedId?Change(p,obj=>obj["release_dates"]!["results"]![0]!["release_dates"]!.AsArray().Add(new JsonObject {
            ["type"]=3,["release_date"]=$"{c.Year+5}-04-01T00:00:00Z",["note"]="Re-release" })):History(p));
        Assert.Equal(CandidateDecision.Accept,accepted.Result.Decision);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task PhysicalOnlyDateIsNotProofOfAnOriginalRelease(bool selected)
    {
        var c=Case();var tested=await Run(c,evidence:p=>(p.Identity.Id==c.SelectedId)==selected?Change(p,obj=>
            obj["release_dates"]!["results"]![0]!["release_dates"]![0]!["type"]=5):History(p));
        Assert.Equal(selected?CandidateDecision.Ambiguous:CandidateDecision.Accept,tested.Result.Decision);
        if(!selected) Assert.Contains("neighbor_original_year_unavailable",tested.Session.Warnings);
    }

    [Fact]
    public async Task ConflictingYearsAndDuplicateExternalIdentityRemainProtected()
    {
        var c=Case();var duplicate=c.Candidates.Single(p=>p.Identity.Id==c.SelectedId) with {Year=c.Year-1};
        Assert.Equal(CandidateDecision.Ambiguous,(await Run(c,candidates:c.Candidates.Append(duplicate).ToArray())).Result.Decision);
        var tested=await Run(c,evidence:p=>Change(p,obj=>obj["imdb_id"]="tt1234567"));
        Assert.Equal(CandidateDecision.Ambiguous,tested.Result.Decision);
        Assert.Equal("external_identity_collision",tested.Session.Outcome);
    }

    [Theory]
    [InlineData("F1",true)] [InlineData("It",true)] [InlineData("418",true)] [InlineData("1917",true)]
    public async Task ExactNumericTitlesCanUseVerifiedOriginalYear(string title,bool accept)
    {
        ProviderSearchCandidate Row(int id,int year)=>new(new("tmdb",SearchMediaType.Movie,id.ToString()),title,year,
            OfficialTitles:[new(title,new("tmdb",SearchMediaType.Movie,id.ToString()),ProviderTitleKind.Original,ProviderTitleSource.SearchOriginalTitle)]);
        var c=new AuditCase(1,$"{title}.2025.1080p.WEB",title,2025,"Film","rss","1",[Row(1,2025),Row(2,2024)]);
        var tested=await Run(c);
        Assert.Equal(accept?CandidateDecision.Accept:CandidateDecision.Ambiguous,tested.Result.Decision);
        if(!accept){Assert.Equal(0,tested.ProofCalls);Assert.Contains(tested.Result.Candidates.SelectMany(x=>x.Contradictions),x=>x.Code=="numeric-title-needs-independent-identity");}
    }

    [Theory]
    [InlineData("coverage")] [InlineData("budget")] [InlineData("series")] [InlineData("manual")]
    [InlineData("established")] [InlineData("hypothesis")] [InlineData("multiple_years")]
    public async Task MissingPreconditionsPreserveTheOriginalMechanism(string mode)
    {
        var c=Case();var ctx=Context(c);var collection=Collection();
        if(mode=="coverage")collection=Collection(6,false);
        if(mode=="budget")collection=Collection(12);
        if(mode=="series")ctx=ctx with {ExpectedMediaType=SearchMediaType.Series};
        if(mode=="manual")ctx=ctx with {HasManualCorrection=true};
        if(mode=="established")ctx=ctx with {EstablishedIdentity=new("tmdb",SearchMediaType.Movie,"99")};
        if(mode=="hypothesis")ctx=ctx with {Queries=ctx.Queries.Select(q=>q with {YearEvidence=CandidateYearEvidence.Hypothesis}).ToArray()};
        if(mode=="multiple_years")c=c with {RawTitle="Oppenheimer.2023.2024.1080p.WEB"};
        var tested=await Run(c,context:ctx,collection:collection);
        Assert.NotEqual(CandidateDecision.Accept,tested.Result.Decision);Assert.Equal(0,tested.ProofCalls);
    }

    [Fact]
    public async Task ConflictingOriginalTitlesAreNotHiddenByAnExactLocalizedTitle()
    {
        var c=Case();var pool=c.Candidates.Select(p=>p.Identity.Id==c.SelectedId?p with {
            OfficialTitles=p.OfficialTitles!.Append(new("Another original title",p.Identity,ProviderTitleKind.Original,ProviderTitleSource.SearchOriginalTitle)).ToArray()
        }:p).ToArray();
        var tested=await Run(c,candidates:pool);
        Assert.Equal(CandidateDecision.Ambiguous,tested.Result.Decision);Assert.Equal(0,tested.ProofCalls);
    }

    [Fact]
    public async Task OptionalEvidenceFailureKeepsLegacyAliasEnrichmentWithinReservedBudget()
    {
        var c=Case(29344);var ctx=Context(c);var calls=0;
        var session=new MovieExactYearPriority(ctx,c.RawTitle,Collection(),c.Candidates,
            (_,_)=>{calls++;throw new HttpRequestException("private-marker");},
            (id,_)=>Task.FromResult<JsonElement?>(Aliases(c.Candidates.Single(p=>p.Identity.Id==id))));
        var enriched=await MovieTitleCandidates.EnrichAsync(ctx,c.Candidates,session.LoadTitlesAsync,default);
        var normal=SearchCandidateRanker.Rank(ctx,enriched);var ranked=await session.ApplyAsync(enriched,normal,default);
        Assert.Equal(CandidateDecision.Ambiguous,ranked.Decision);Assert.Equal(normal,ranked);
        Assert.InRange(session.Requests,6,12);Assert.InRange(calls,1,2);
    }

    [Fact]
    public async Task AliasOnlyOrNonExactContenderCannotBeSilentlyExcluded()
    {
        var c=Case();var pool=c.Candidates.Select(p=>p.Identity.Id=="1634345"?p with { OfficialTitles=[new("Oppenheimer",p.Identity,ProviderTitleKind.Alternative,ProviderTitleSource.AlternativeTitlesEndpoint)]}:p).ToArray();
        var tested=await Run(c,candidates:pool);
        Assert.Equal(CandidateDecision.Ambiguous,tested.Result.Decision);Assert.Equal(0,tested.ProofCalls);
    }
}
