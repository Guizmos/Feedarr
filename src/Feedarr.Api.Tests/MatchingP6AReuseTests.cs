using System.Net;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Models;

namespace Feedarr.Api.Tests;

public sealed class MatchingP6AReuseTests
{
    private const string Raw="The.Matrix.1999.1080p.BluRay.x264-GRP";
    private static long Target(PosterMatchingContractTestRig rig,string raw=Raw,string clean="Matrix")=>rig.CreateRelease(raw,clean,1999,UnifiedCategory.Film,"movie");
    private static long Root(PosterMatchingContractTestRig rig,bool entityVerified=true,bool ownVerified=true,string language="fr")
    {
        var id=Target(rig);using var db=rig.Database.Open();
        var entity=db.ExecuteScalar<long>("""
            INSERT INTO media_entities(unified_category,title_clean,year,created_at_ts,updated_at_ts,tmdb_id,tmdb_media_type,
                ext_snapshot_version,ext_provider,ext_provider_id,ext_title,ext_release_date,poster_file)
            VALUES('Film','root',1999,0,0,100,'movie',@version,'tmdb','100','The Matrix','1999-01-01','root.jpg') RETURNING id
            """,new{version=entityVerified?(int?)1:null});
        db.Execute("""
            UPDATE releases SET entity_id=@entity,tmdb_id=100,tmdb_media_type='movie',ext_provider='tmdb',ext_provider_id='100',
                ext_snapshot_version=@version,poster_file='root.jpg',poster_path='/root.jpg',poster_provider='tmdb',poster_provider_id='100',poster_lang=@language WHERE id=@id
            """,new{entity,id,version=ownVerified?(int?)1:null,language});
        Directory.CreateDirectory(rig.PostersDirectory);File.WriteAllBytes(Path.Combine(rig.PostersDirectory,"root.jpg"),PosterTestImage.Create());return id;
    }

    [Fact]
    public void AVerifiedEntityRootAndStrongMetadataDoNotNeedADuplicateReleaseSnapshot()
    {
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        Root(rig,ownVerified:false);var id=Target(rig);
        Assert.True(rig.Repository.TryReuseLocalIdentity(id).Reused);
        Assert.Equal(100,rig.Repository.GetForPoster(id)!.TmdbId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbsentSecondaryAliasesAreUnknownButDifferentPositiveAliasesAreConflicts(bool positiveConflict)
    {
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        var root=Root(rig);var id=Target(rig);using var db=rig.Database.Open();
        db.Execute("UPDATE releases SET raw_json=@json WHERE id=@root",new{root,json="{\"Version\":1,\"TmdbId\":100,\"TvdbId\":999,\"ImdbId\":\"tt1234567\"}"});
        if(positiveConflict)db.Execute("UPDATE media_entities SET tvdb_id=123,imdb_id='tt7654321' WHERE id=(SELECT entity_id FROM releases WHERE id=@root)",new{root});
        var result=rig.Repository.TryReuseLocalIdentity(id);Assert.Equal(!positiveConflict,result.Reused);
        if(positiveConflict)Assert.Equal("contradictory_donor_claims",result.Reason);
    }

    [Fact]
    public void FanartIsAnImageProviderForTheSameTmdbWorkNotAnIdentityContradiction()
    {
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        var root=Root(rig);var id=Target(rig);using var db=rig.Database.Open();
        db.Execute("UPDATE releases SET poster_provider='fanart',poster_provider_id='movie:100' WHERE id=@root",new{root});
        var result=rig.Repository.TryReuseLocalIdentity(id);Assert.True(result.Reused);Assert.True(result.ImageReusable);
        var row=rig.Repository.GetForPoster(id)!;Assert.Equal("tmdb",row.ExtProvider);Assert.Equal("fanart",row.PosterProvider);Assert.Equal("movie:100",row.PosterProviderId);
    }

    [Fact]
    public async Task UntrustedPosterDoesNotVetoIdentityAndIsReplacedUsingTheSameWork()
    {
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit,completeTmdbMetadata:true);
        var root=Root(rig);var id=Target(rig);using var db=rig.Database.Open();
        db.Execute("UPDATE releases SET poster_provider_id='999' WHERE id=@root",new{root});
        Assert.True((await rig.FetchAsync(id)).Ok);
        var row=rig.Repository.GetForPoster(id)!;Assert.Equal(100,row.TmdbId);Assert.Equal("fr",row.PosterLang);Assert.NotEqual("root.jpg",row.PosterFile);
        Assert.Empty(rig.TmdbSearchRequests);
    }

    [Fact]
    public async Task HistoricalValidationIsTargetedCachedAndReplacesUnverifiedMetadataWithoutPromotingDescendants()
    {
        var details=0;
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit,completeTmdbMetadata:true,
            tmdbResponse:(request,_)=>{
                if(request.RequestUri!.AbsolutePath.EndsWith("/movie/100")) {details++;return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK){Content=new StringContent("{\"id\":100,\"title\":\"The Matrix\",\"release_date\":\"1999-03-31\"}")});}
                return Task.FromResult<HttpResponseMessage?>(null);
            });
        Root(rig,false,false);var id=Target(rig);
        Assert.True((await rig.FetchAsync(id)).Ok);
        var calls=details;Assert.True(calls>0);Assert.Empty(rig.TmdbSearchRequests);
        Assert.Equal(1,rig.Repository.GetForPoster(id)!.ExtSnapshotVersion);
        var second=Target(rig);Assert.True((await rig.FetchAsync(second)).Ok);Assert.Equal(calls,details);
        using var db=rig.Database.Open();Assert.Equal(2,db.ExecuteScalar<int>("SELECT COUNT(*) FROM local_identity_reuse"));
        Assert.Equal(1,db.ExecuteScalar<int>("SELECT COUNT(DISTINCT donor_release_id) FROM local_identity_reuse"));
    }

    [Fact]
    public void HistoricalValidationCannotOutliveAManualCorrection()
    {
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        var root=Root(rig,false,false);var target=Target(rig);var before=rig.Repository.GetForPoster(root)!;
        rig.Repository.SetManualMatch(root,"tmdb","movie","999");
        var details=new Feedarr.Api.Services.Tmdb.TmdbClient.DetailsResult("The Matrix",null,null,null,"1999-03-31",null,null,null,null,null,null);
        Assert.False(rig.Repository.TryReuseLocalIdentity(target,before,details).Reused);
        Assert.Null(rig.Repository.GetForPoster(target)!.EntityId);Assert.Equal("999",rig.Repository.GetForPoster(root)!.ManualMatchId);
    }

    [Fact]
    public void ALegacyManualPosterOnTheEntityIsNeverUsedAsAnAutomaticRoot()
    {
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        var root=Root(rig);var target=Target(rig);using var db=rig.Database.Open();
        db.Execute("UPDATE releases SET poster_file=NULL WHERE id=@root; UPDATE media_entities SET poster_file='tmdb-100-manual.jpg' WHERE id=(SELECT entity_id FROM releases WHERE id=@root)",new{root});
        var result=rig.Repository.TryReuseLocalIdentity(target);Assert.False(result.Reused);Assert.Equal("donor_unverified_or_manual",result.Reason);
    }

    [Fact]
    public async Task FailedHistoricalTitleValidationDoesNotPromoteTheWrongIdentity()
    {
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit,completeTmdbMetadata:true,
            tmdbResponse:(request,_)=>Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.EndsWith("/movie/999")?
                new(HttpStatusCode.OK){Content=new StringContent("{\"id\":999,\"title\":\"Different Work\",\"release_date\":\"1999-01-01\"}")}:null));
        var root=Root(rig,false,false);var target=Target(rig);
        using(var db=rig.Database.Open())db.Execute("UPDATE releases SET tmdb_id=999,ext_provider_id='999',poster_provider_id='999' WHERE id=@root; UPDATE media_entities SET tmdb_id=999,ext_provider_id='999' WHERE id=(SELECT entity_id FROM releases WHERE id=@root)",new{root});
        Assert.True((await rig.FetchAsync(target)).Ok);Assert.Equal(100,rig.Repository.GetForPoster(target)!.TmdbId);
        Assert.Equal(999,rig.Repository.GetForPoster(root)!.TmdbId);Assert.Null(rig.Repository.GetForPoster(root)!.ExtSnapshotVersion);
        using var connection=rig.Database.Open();Assert.Equal(0,connection.ExecuteScalar<int>("SELECT COUNT(*) FROM local_identity_reuse"));
    }

    [Fact]
    public async Task P4EnglishImageIsAFallbackWhileFrenchIsPreferredOnTheSameIdentity()
    {
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit,completeTmdbMetadata:true);
        Root(rig,language:"en");var id=Target(rig);
        Assert.True((await rig.FetchAsync(id)).Ok);var row=rig.Repository.GetForPoster(id)!;
        Assert.Equal(100,row.TmdbId);Assert.Equal("fr",row.PosterLang);Assert.NotEqual("root.jpg",row.PosterFile);
    }

    [Fact]
    public async Task AVerifiedEnglishFileSurvivesWhenImageSelectionIsUnavailable()
    {
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit,completeTmdbMetadata:true,
            tmdbResponse:(request,_)=>Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.EndsWith("/images")?new(HttpStatusCode.ServiceUnavailable):null));
        Root(rig,language:"en");var id=Target(rig);Assert.True((await rig.FetchAsync(id)).Ok);
        var row=rig.Repository.GetForPoster(id)!;Assert.Equal(100,row.TmdbId);Assert.Equal("root.jpg",row.PosterFile);Assert.Equal("en",row.PosterLang);
    }

    [Fact]
    public async Task ImageSharingDoesNotBypassFrenchSelectionAndStillCachesTheSelection()
    {
        var images=0;var downloads=0;
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit,completeTmdbMetadata:true,tmdbResponse:(request,_)=>{
            if(request.RequestUri!.AbsolutePath.EndsWith("/images"))images++;
            if(request.RequestUri.Host=="image.tmdb.org")downloads++;
            return Task.FromResult<HttpResponseMessage?>(null);
        });
        var first=Target(rig,clean:"independent one");Assert.True((await rig.FetchAsync(first)).Ok);
        using(var db=rig.Database.Open())db.Execute("UPDATE releases SET poster_lang='en',poster_path='/english.jpg' WHERE id=@first",new{first});
        var second=Target(rig,Raw.Replace("1080p","720p"),"independent two");Assert.True((await rig.FetchAsync(second)).Ok);
        Assert.Equal("fr",rig.Repository.GetForPoster(second)!.PosterLang);Assert.Equal(2,downloads);Assert.Equal(1,images);
        var third=Target(rig,Raw.Replace("1080p","2160p"),"independent three");Assert.True((await rig.FetchAsync(third)).Ok);
        Assert.Equal("fr",rig.Repository.GetForPoster(third)!.PosterLang);Assert.Equal(2,downloads);Assert.Equal(1,images);
    }

    [Fact]
    public async Task EnglishImageCanBeSharedAfterTheCachedSelectionConfirmsThereIsNoFrenchImage()
    {
        var downloads=0;var images=0;
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit,completeTmdbMetadata:true,tmdbResponse:(request,_)=>{
            if(request.RequestUri!.AbsolutePath.EndsWith("/images")) {images++;return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK){Content=new StringContent("{\"id\":100,\"posters\":[{\"file_path\":\"/english.jpg\",\"iso_639_1\":\"en\"}]}")});}
            if(request.RequestUri.Host=="image.tmdb.org")downloads++;
            return Task.FromResult<HttpResponseMessage?>(null);
        });
        var first=Target(rig,clean:"first");Assert.True((await rig.FetchAsync(first)).Ok);
        var second=Target(rig,Raw.Replace("1080p","720p"),"second");Assert.True((await rig.FetchAsync(second)).Ok);
        Assert.Equal("en",rig.Repository.GetForPoster(second)!.PosterLang);Assert.Equal(1,images);Assert.Equal(1,downloads);
    }
}
