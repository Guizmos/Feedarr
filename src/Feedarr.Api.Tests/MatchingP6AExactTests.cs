using System.Text.Json;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Posters;

namespace Feedarr.Api.Tests;

public sealed class MatchingP6AExactTests
{
    private static ProviderSearchCandidate Candidate(string id,string title,int year) => new(new("tmdb",SearchMediaType.Movie,id),title,year,
        OfficialTitles:[new(title,new("tmdb",SearchMediaType.Movie,id),ProviderTitleKind.Original,ProviderTitleSource.SearchOriginalTitle),
            new(title,new("tmdb",SearchMediaType.Movie,id),ProviderTitleKind.Localized,ProviderTitleSource.SearchTitle,"fr-FR")]);
    private static JsonElement Proof(ProviderSearchCandidate c,int? firstYear=null,string? note=null,int secondaryType=5,int? secondaryYear=null,string? imdb=null) => JsonSerializer.SerializeToElement(new {
        id=int.Parse(c.Identity.Id),title=c.Title,original_title=c.Title,status="Released",release_date=$"{c.Year}-01-01",imdb_id=imdb,
        alternative_titles=new{id=int.Parse(c.Identity.Id),titles=Array.Empty<object>()},
        release_dates=new{results=new[]{new{iso_3166_1="US",release_dates=new[]{
            new{type=1,release_date=$"{firstYear??c.Year}-01-01T00:00:00Z",note=""},
            new{type=3,release_date=$"{c.Year}-07-01T00:00:00Z",note=note??""},
            new{type=secondaryType,release_date=$"{secondaryYear??c.Year}-11-03T00:00:00Z",note="Re-release"}}}}}
    });
    private static async Task<(CandidateRankingResult Result,MovieExactYearPriority Session,int Calls)> Run(string raw,ProviderSearchCandidate[] pool,
        Func<ProviderSearchCandidate,JsonElement>? proof=null)
    {
        var context=new CandidateRankingContext(SearchCandidateGenerator.Generate(new(raw,UnifiedCategory.Film)),UnifiedCategory.Film,SearchMediaType.Movie);
        var calls=0;var collection=new VideoSearchCollection([],null,new(2,6,6,pool.Length),6,"targeted_year");
        var session=new MovieExactYearPriority(context,raw,collection,pool,(id,_)=>{calls++;var c=pool.Single(c=>c.Identity.Id==id);return Task.FromResult<JsonElement?>(proof?.Invoke(c)??Proof(c));},(_,_)=>throw new InvalidOperationException("No duplicate alias request"));
        return (await session.ApplyAsync(pool,SearchCandidateRanker.Rank(context,pool),default),session,calls);
    }

    [Theory]
    [InlineData("418.2026.FRENCH.1080p.WEB.H264","418",2026,"1606527")]
    [InlineData("9.11.The.Final.Minutes.Of.Flight.93.2020.FRENCH.1080p.HDTV.x265","9/11: The Final Minutes of Flight 93",2020,"742159")]
    public async Task ExactUniqueNumericIdentityUsesContextInsteadOfAFuzzyVeto(string raw,string title,int year,string id)
    {
        var result=await Run(raw,[Candidate(id,title,year)]);
        Assert.Equal(CandidateDecision.Accept,result.Result.Decision);Assert.Equal(id,result.Result.SelectedIdentity!.Id);
        Assert.Equal(1,result.Calls);Assert.Contains(result.Session.Warnings,w=>w.Contains("numeric"));
    }

    [Fact]
    public async Task OppenheimerEditionAndSecondaryRereleaseCannotChangeWorkIdentity()
    {
        var result=await Run("Oppenheimer.2023.REMASTERED.1080p.WEB.H264",[Candidate("872585","Oppenheimer",2023),Candidate("1634345","Oppenheimer",2024)],
            c=>Proof(c,note:c.Year==2023?"IMAX remastered edition":null,secondaryYear:2023));
        Assert.Equal("872585",result.Result.SelectedIdentity!.Id);Assert.Equal(CandidateDecision.Accept,result.Result.Decision);
        Assert.Contains("video_release_edition",result.Session.Warnings);Assert.Contains("video_edition_same_identity",result.Session.Warnings);
        Assert.Contains("secondary_release_in_requested_year",result.Session.Warnings);
    }

    [Theory]
    [InlineData("Overdose",2022,"1024766","896485")]
    [InlineData("Gentle Monster",2026,"1473148","1830792")]
    public async Task SameYearExactHomonymsNeverGetArbitraryPriority(string title,int year,string a,string b)
    {
        var result=await Run($"{title}.{year}.1080p.WEB.H264",[Candidate(a,title,year),Candidate(b,title,year)]);
        Assert.Equal(CandidateDecision.Ambiguous,result.Result.Decision);Assert.Equal(0,result.Calls);
        Assert.Equal("exact_year_identity_not_unique",result.Session.Outcome);
    }

    [Theory]
    [InlineData("Bruised",2020,"1412141","718633")]
    [InlineData("Whistle",2025,"1526225","1193501")]
    public async Task ANeighborsEarlierOriginalPremiereIsAnIdentityCompetitor(string title,int year,string a,string b)
    {
        var result=await Run($"{title}.{year}.1080p.WEB.H264",[Candidate(a,title,year),Candidate(b,title,year+1)],
            c=>Proof(c,firstYear:year));
        Assert.Equal(CandidateDecision.Ambiguous,result.Result.Decision);Assert.Equal("neighbor_original_year_competes",result.Session.Outcome);
        Assert.Contains($"tmdb={b}",result.Session.ConflictEvidence);Assert.Contains($"firstOriginalYear={year}",result.Session.ConflictEvidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PieceByPieceDistinguishesSecondaryReleaseFromExternalIdentityCollision(bool sameImdb)
    {
        var result=await Run("Piece.by.Piece.2024.1080p.WEB.H264",[Candidate("1236419","Piece by Piece",2024),Candidate("1133973","Piece by Piece",2023)],
            c=>Proof(c,secondaryYear:2024,imdb:sameImdb?"tt1234567":null));
        Assert.Equal(sameImdb?CandidateDecision.Ambiguous:CandidateDecision.Accept,result.Result.Decision);
        if(sameImdb){Assert.Equal("external_identity_collision",result.Session.Outcome);Assert.Contains("sameImdb=True",result.Session.ConflictEvidence);}
        else Assert.Contains("secondary_release_in_requested_year",result.Session.Warnings);
    }

    [Fact]
    public async Task ContextualPunctuationCannotHideASecondExactIdentityBehindALegacyAccept()
    {
        var result=await Run("9.11.Example.2025.1080p.WEB.H264",[Candidate("1","9 11 Example",2025),Candidate("2","9/11 Example",2025)]);
        Assert.Equal(CandidateDecision.Ambiguous,result.Result.Decision);Assert.Null(result.Result.SelectedIdentity);Assert.Equal(0,result.Calls);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("type")]
    [InlineData("year")]
    [InlineData("title")]
    public async Task PositiveNeighborIdentityConflictsAreNotDowngradedToMissingHistory(string conflict)
    {
        var result=await Run("Example.2025.1080p.WEB.H264",[Candidate("1","Example",2025),Candidate("2","Example",2024)],c=>{
            var root=System.Text.Json.Nodes.JsonNode.Parse(Proof(c).GetRawText())!.AsObject();
            if(c.Identity.Id=="2")
            {
                if(conflict=="owner")root["id"]=999;
                if(conflict=="type")root["media_type"]="tv";
                if(conflict=="year")root["release_date"]="2023-01-01";
                if(conflict=="title")root["original_title"]="Different work";
            }
            return JsonSerializer.SerializeToElement(root);
        });
        Assert.Equal(CandidateDecision.Ambiguous,result.Result.Decision);Assert.NotEqual("applied",result.Session.Outcome);
    }

    [Fact]
    public async Task AWeakerNeighborsMissingHistoryIsNotAnIdentityContradiction()
    {
        var pool=new[]{Candidate("1","Example",2025),Candidate("2","Example",2024)};
        var context=new CandidateRankingContext(SearchCandidateGenerator.Generate(new("Example.2025.1080p.WEB.H264",UnifiedCategory.Film)),UnifiedCategory.Film,SearchMediaType.Movie);
        var session=new MovieExactYearPriority(context,"Example.2025.1080p.WEB.H264",new([],null,new(2,6,6,2),6,"targeted_year"),pool,
            (id,_)=>Task.FromResult<JsonElement?>(id=="1"?Proof(pool[0]):null),(_,_)=>throw new InvalidOperationException());
        var result=await session.ApplyAsync(pool,SearchCandidateRanker.Rank(context,pool),default);
        Assert.Equal(CandidateDecision.Accept,result.Decision);Assert.Contains("neighbor_history_unavailable",session.Warnings);
    }

    [Fact]
    public async Task WordsInsideAnOfficialTitleAreNotEditionEvidence()
    {
        var result=await Run("Restoration.2025.1080p.WEB.H264",[Candidate("1","Restoration",2025),Candidate("2","Restoration",2024)]);
        Assert.Equal(CandidateDecision.Accept,result.Result.Decision);Assert.DoesNotContain("video_release_edition",result.Session.Warnings);
    }

    [Fact]
    public async Task NumericPunctuationUsesTheCompleteTargetedCollectionWithoutRedundantGeneralSearch()
    {
        const string raw="9.11.The.Final.Minutes.Of.Flight.93.2020.1080p.HDTV.x265";
        var context=new CandidateRankingContext(SearchCandidateGenerator.Generate(new(raw,UnifiedCategory.Film)),UnifiedCategory.Film,SearchMediaType.Movie);
        var collection=await MovieYearSearchCollector.CollectAsync(context,["fr-FR"],(_,_,_,year,_)=>{
            Assert.NotNull(year);
            Feedarr.Api.Services.Tmdb.TmdbClient.SearchResult[] rows=year==2020?[new(742159,"9/11: The Final Minutes of Flight 93","9/11: The Final Minutes of Flight 93",null,"movie",2020,"en")]:[];
            return Task.FromResult<Feedarr.Api.Services.Tmdb.TmdbClient.VideoMatchingPage?>(new(rows,rows.Length,rows.Length));
        },default);
        Assert.Equal("targeted_year",collection.SearchMode);Assert.True(collection.Complete);Assert.Equal(3,collection.Counters.SearchRequests);
    }

    [Fact]
    public async Task NumericContextStillRequiresCompleteCollectionAndReliableYear()
    {
        var query=new SearchCandidate("418",2026,CandidateYearEvidence.Hypothesis,SearchCandidateOrigin.AmbiguousTrailingNumber);
        var context=new CandidateRankingContext([query],UnifiedCategory.Film,SearchMediaType.Movie);var pool=new[]{Candidate("1606527","418",2026)};
        var session=new MovieExactYearPriority(context,"418 2026",new([],"partial",new(1,1,1,1),2,"targeted_year"),pool,(_,_)=>throw new InvalidOperationException(),(_,_)=>throw new InvalidOperationException());
        Assert.NotEqual(CandidateDecision.Accept,(await session.ApplyAsync(pool,SearchCandidateRanker.Rank(context,pool),default)).Decision);
    }
}
