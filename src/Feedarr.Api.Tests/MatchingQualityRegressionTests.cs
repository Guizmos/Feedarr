using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Tests;

public sealed class MatchingQualityRegressionTests
{
    [Theory]
    [InlineData("Handmaids Tale", "Handmaid's Tale")]
    [InlineData("Chroniques sexuelles dune famille daujourdhui", "Chroniques sexuelles d’une famille d’aujourd’hui")]
    [InlineData("Lhomme", "Lʼhomme")]
    public void MissingIntraWordApostrophesKeepOfficialTitleIdentity(string release,string official)
    {
        Assert.Equal(TitleNormalizer.NormalizeApostropheOmissions(official),TitleNormalizer.NormalizeApostropheOmissions(release));
        Assert.NotEqual(TitleNormalizer.NormalizeTitleStrict("O Connor"),TitleNormalizer.NormalizeTitleStrict("OConnor"));
        Assert.NotEqual(TitleNormalizer.NormalizeTitleStrict("12/12"),TitleNormalizer.NormalizeTitleStrict("1212"));
    }

    [Theory]
    [InlineData("Le Baron de l ecluse", "Le Baron de l'écluse")]
    [InlineData("Quand l inspecteur s emmele", "Quand l'inspecteur s'emmêle")]
    [InlineData("Handmaids Tale", "Handmaid's Tale")]
    public void BothSpacedAndOmittedApostropheFormsRetainExactOfficialMatching(string query,string title)
    {
        var id=new SearchIdentity("tmdb",SearchMediaType.Movie,"1");
        var context=new CandidateRankingContext(new[]{new SearchCandidate(query,2012,CandidateYearEvidence.Parsed,SearchCandidateOrigin.Primary)},UnifiedCategory.Film,SearchMediaType.Movie);
        Assert.Equal(CandidateDecision.Accept,SearchCandidateRanker.Rank(context,new[]{new ProviderSearchCandidate(id,title,2012)}).Decision);
    }

    [Theory]
    [InlineData("Avengers.Endgame.Extended.2019.EXTENDED.VFF.2160p.BluRay.x265-GRP","Avengers Endgame",2019)]
    [InlineData("Chacun.pour.tous.2018.AD.VOF.1080p.AMZN.WEB-DL.H.264-GRP","Chacun pour tous",2018)]
    [InlineData("Extended.2019.VFF.1080p.BluRay.x264-GRP","Extended",2019)]
    [InlineData("A.D.2015.VFF.1080p.BluRay.x264-GRP","A D",2015)]
    public void TechnicalSuffixEvidenceDoesNotRemoveSignificantTitleWords(string raw,string title,int year)
    {
        var p=new TitleParser().Parse(raw,UnifiedCategory.Film); Assert.Equal(title,p.TitleClean); Assert.Equal(year,p.Year);
    }

    private static readonly SearchIdentity Id=new("tmdb",SearchMediaType.Series,"1396");
    private static readonly SearchIdentity Other=new("tvmaze",SearchMediaType.Series,"169");
    private static CandidateRankingContext Context => new(new[]{new SearchCandidate("Breaking Bad",null,CandidateYearEvidence.None,SearchCandidateOrigin.Primary)},UnifiedCategory.Serie,SearchMediaType.Series);
    private static ProviderSearchCandidate Show(bool corroborated) => new(Id,"Breaking Bad",2008,
        KnownExternalIdentities:new[]{Other},Television:new(Id,null,null,CorroboratingIdentity:corroborated ? Other : null));

    [Fact]
    public void VerifiedCrossCatalogueIdentitySupportsAnExactUniqueSeriesWithoutInventingAYear()
    {
        Assert.Equal(CandidateDecision.Ambiguous,SearchCandidateRanker.Rank(Context,new[]{Show(false)}).Decision);
        var result=SearchCandidateRanker.Rank(Context,new[]{Show(true)});
        Assert.Equal(CandidateDecision.Accept,result.Decision); Assert.Equal(Id,result.SelectedIdentity);
        Assert.Contains("verified-cross-provider-tv-identity",result.Candidates.Single().PositiveReasons);
    }

    [Fact]
    public void SharedCorroborationDoesNotBreakATieBetweenRemakes()
    {
        var other=new SearchIdentity("tmdb",SearchMediaType.Series,"2");
        var otherLink=new SearchIdentity("tvmaze",SearchMediaType.Series,"3");
        var remake=Show(true) with { Identity=other,Year=2020,KnownExternalIdentities=new[]{otherLink},Television=new(other,null,null,CorroboratingIdentity:otherLink) };
        Assert.Equal(CandidateDecision.Ambiguous,SearchCandidateRanker.Rank(Context,new[]{Show(true),remake}).Decision);
    }

    [Fact]
    public void CorroborationCannotReplaceExplicitEpisodeEvidenceOrOverrideIdentityGuards()
    {
        Assert.Equal(CandidateDecision.Ambiguous,SearchCandidateRanker.Rank(Context with {Season=1,Episode=2},new[]{Show(true)}).Decision);
        Assert.True(SearchCandidateRanker.Rank(Context with {EstablishedIdentity=Id},new[]{Show(true)}).PreserveExisting);
        var wrong=Show(true) with {Television=new(Id,null,null,CorroboratingIdentity:Other with {MediaType=SearchMediaType.Movie})};
        Assert.Equal(CandidateDecision.Ambiguous,SearchCandidateRanker.Rank(Context,new[]{wrong}).Decision);
    }
    [Theory]
    [InlineData(2008, CandidateDecision.Accept)]
    [InlineData(2009, CandidateDecision.Accept)]
    [InlineData(2020, CandidateDecision.Ambiguous)]
    public async Task CrossCatalogueSupportRequiresConsistentCatalogueYears(int otherYear,object expected)
    {
        System.Text.Json.JsonElement Json(string value) { using var document=System.Text.Json.JsonDocument.Parse(value); return document.RootElement.Clone(); }
        var rows=await TelevisionCandidates.EnrichAsync(Context.Queries,UnifiedCategory.Serie,"Breaking Bad",new[]{new ProviderSearchCandidate(Id,"Breaking Bad",2008)},
            (provider,path,_)=>Task.FromResult<System.Text.Json.JsonElement?>(provider=="tmdb"
                ? Json("{\"id\":1396,\"name\":\"Breaking Bad\",\"first_air_date\":\"2008-01-01\",\"external_ids\":{\"tvdb_id\":81189}}")
                : path.StartsWith("search/")
                    ? Json("[{\"show\":{\"id\":169,\"name\":\"Breaking Bad\",\"premiered\":\""+otherYear+"-01-01\"}}]")
                    : Json("{\"id\":169,\"name\":\"Breaking Bad\",\"premiered\":\""+otherYear+"-01-01\",\"externals\":{\"thetvdb\":81189}}")),true,default);
        Assert.Equal(expected,SearchCandidateRanker.Rank(Context,rows).Decision);
    }

    [Fact]
    public void ApostropheVariantsCannotCreateFakeIndependentCompoundNames()
    {
        var id=new SearchIdentity("tmdb",SearchMediaType.Movie,"1");
        var row=new ProviderSearchCandidate(id,"Handmaid's Tale",2012,OfficialTitles:new[]{
            new OfficialProviderTitle("Handmaid's Tale",id,ProviderTitleKind.Original,ProviderTitleSource.SearchOriginalTitle,"en"),
            new OfficialProviderTitle("Handmaid’s Tale",id,ProviderTitleKind.Localized,ProviderTitleSource.SearchTitle,"fr-FR") });
        var context=new CandidateRankingContext(new[]{new SearchCandidate("Handmaids Tale Handmaids Tale",2012,CandidateYearEvidence.Parsed,SearchCandidateOrigin.Primary)},UnifiedCategory.Film,SearchMediaType.Movie);
        Assert.Equal(CandidateDecision.Reject,SearchCandidateRanker.Rank(context,new[]{row}).Decision);
    }

}
