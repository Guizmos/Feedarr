using System.Net;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Models;
using Xunit.Abstractions;

namespace Feedarr.Api.Tests;

public sealed class SharedVideoPublicationTests(ITestOutputHelper output)
{
    private static long Movie(PosterMatchingContractTestRig rig) => rig.CreateRelease("The.Matrix.1999.1080p.BluRay", "The Matrix", 1999, UnifiedCategory.Film, "movie");
    private static int StoreFileCount(PosterMatchingContractTestRig rig) => Directory.GetFiles(Path.Combine(rig.PostersDirectory, "store"), "*", SearchOption.AllDirectories).Length;

    [Fact]
    public async Task TenIndependentMovieDecisionsShareOnlyVerifiedMetadataAndPhysicalAssets()
    {
        int sharedDetails=0, sharedImages=0, sharedDownloads=0, freshDetails=0, freshImages=0, freshDownloads=0;
        PosterMatchingContractTestRig Rig(bool shared) => new(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata:true,
            tmdbResponse:(request, _) => {
                var uri=request.RequestUri!;
                if (uri.Host=="image.tmdb.org") { if(shared) sharedDownloads++; else freshDownloads++; }
                if (uri.AbsolutePath.EndsWith("/images")) { if(shared) sharedImages++; else freshImages++; }
                if (uri.AbsolutePath.EndsWith("/100") || uri.AbsolutePath.EndsWith("/credits")) { if(shared) sharedDetails++; else freshDetails++; }
                return Task.FromResult<HttpResponseMessage?>(null);
            });
        using var cached=Rig(true);
        string? file=null; Dictionary<string,DateTime>? originalTimes=null;
        for(int i=0;i<10;i++)
        {
            using var fresh=Rig(false);
            var a=Movie(cached); var b=Movie(fresh);
            var actual=await cached.FetchAsync(a); var expected=await fresh.FetchAsync(b);
            Assert.True(expected.Ok); Assert.True(actual.Ok,JsonSerializer.Serialize(actual.Body));
            var ar=cached.GetReleaseForPoster(a)!; var br=fresh.GetReleaseForPoster(b)!;
            Assert.Equal(br.TmdbId,ar.TmdbId); Assert.Equal(br.TmdbMediaType,ar.TmdbMediaType);
            Assert.Equal(br.ExtTitle,ar.ExtTitle); Assert.Equal(br.ExtOverview,ar.ExtOverview);
            Assert.Equal(br.ExtDirectors,ar.ExtDirectors); Assert.Equal(br.ExtSnapshotVersion,ar.ExtSnapshotVersion);
            if(i==0)
            {
                file=ar.PosterFile;
                originalTimes=Directory.GetFiles(Path.Combine(cached.PostersDirectory,"store"),"*",SearchOption.AllDirectories).ToDictionary(x=>x,File.GetLastWriteTimeUtc);
            }
            else
            {
                Assert.True(JsonSerializer.SerializeToElement(actual.Body).GetProperty("reused").GetBoolean());
                Assert.Equal(file,ar.PosterFile);
                foreach(var item in originalTimes!) Assert.Equal(item.Value,File.GetLastWriteTimeUtc(item.Key));
            }
            Assert.Equal(4,StoreFileCount(fresh));
        }
        Assert.Equal(20,freshDetails); Assert.Equal(2,sharedDetails);
        Assert.Equal(10,freshImages); Assert.Equal(1,sharedImages);
        Assert.Equal(10,freshDownloads); Assert.Equal(1,sharedDownloads);
        Assert.Equal(4,StoreFileCount(cached));
        using var db=cached.Database.Open(); Assert.Equal(10,db.ExecuteScalar<int>("SELECT COUNT(*) FROM poster_store_refs"));
        output.WriteLine("10 independent movie acceptances: details/credits 20->2; image selection 10->1; downloads 10->1; full materializations 10->1 (unchanged original + 3 thumbnail mtimes). External network=0.");
    }

    [Fact]
    public async Task TvmazeReusesTheAcceptedShowsPosterAndKeepsEachReleaseIndependent()
    {
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeHit,completeTmdbMetadata:true);
        string? file=null;
        for(int i=0;i<10;i++)
        {
            var id=rig.CreateRelease("Show Tvmaze Hit (2015)","Show Tvmaze Hit",2015,UnifiedCategory.Serie,"series");
            var result=await rig.FetchAsync(id); Assert.True(result.Ok,JsonSerializer.Serialize(result.Body));
            var row=rig.GetReleaseForPoster(id)!; Assert.Equal("tvmaze",row.ExtProvider); Assert.Equal("3001",row.ExtProviderId);
            if(i==0) file=row.PosterFile; else { Assert.Equal(file,row.PosterFile); Assert.True(JsonSerializer.SerializeToElement(result.Body).GetProperty("reused").GetBoolean()); }
        }
        Assert.Equal(1,rig.TvmazeImageRequests); Assert.Equal(4,StoreFileCount(rig));
    }

    [Fact]
    public async Task ExplicitRefreshStillDownloadsAndRefreshesVerifiedMetadata()
    {
        int downloads=0,details=0;
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit,completeTmdbMetadata:true,tmdbResponse:(request,_)=>{
            if(request.RequestUri!.Host=="image.tmdb.org") downloads++;
            if(request.RequestUri.AbsolutePath.EndsWith("/100") || request.RequestUri.AbsolutePath.EndsWith("/credits")) details++;
            return Task.FromResult<HttpResponseMessage?>(null);
        });
        var id=Movie(rig); Assert.True((await rig.FetchAsync(id)).Ok); var before=rig.GetReleaseForPoster(id)!;
        var result=await rig.Posters.FetchPosterAsync(id,default,false,skipIfExists:false);
        Assert.True(result.Ok); Assert.Equal(2,downloads); Assert.Equal(4,details);
        Assert.NotEqual(before.PosterFile,rig.GetReleaseForPoster(id)!.PosterFile);
        Assert.Equal(before.TmdbId,rig.GetReleaseForPoster(id)!.TmdbId);
    }

    [Theory]
    [InlineData("original")]
    [InlineData("thumbnail")]
    [InlineData("legacy")]
    [InlineData("namespace")]
    public async Task IncompatibleOrDamagedAssetsAreNotReused(string defect)
    {
        int downloads=0;
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit,completeTmdbMetadata:true,tmdbResponse:(request,_)=>{
            if(request.RequestUri!.Host=="image.tmdb.org") downloads++; return Task.FromResult<HttpResponseMessage?>(null);
        });
        var first=Movie(rig); Assert.True((await rig.FetchAsync(first)).Ok); var donor=rig.GetReleaseForPoster(first)!;
        var dir=Path.Combine(rig.PostersDirectory,"store",donor.PosterStoreDir!);
        if(defect=="original") File.WriteAllBytes(Directory.GetFiles(dir,"original.*").Single(),new byte[]{1,2,3});
        if(defect=="thumbnail") File.WriteAllBytes(Path.Combine(dir,"w500.webp"),Array.Empty<byte>());
        if(defect=="legacy") File.Delete(Path.Combine(rig.PostersDirectory,donor.PosterFile!));
        if(defect=="namespace") { using var db=rig.Database.Open(); db.Execute("UPDATE releases SET tmdb_media_type='tv' WHERE id=@id",new{id=first}); }
        var next=Movie(rig); var result=await rig.FetchAsync(next);
        Assert.True(result.Ok,JsonSerializer.Serialize(result.Body)); Assert.Equal(2,downloads);
        Assert.NotEqual(donor.PosterFile,rig.GetReleaseForPoster(next)!.PosterFile);
    }

    [Fact]
    public async Task ReuseRollbackDoesNotPublishAReferenceOrDamageTheDonor()
    {
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit,completeTmdbMetadata:true);
        var first=Movie(rig); Assert.True((await rig.FetchAsync(first)).Ok); var donor=rig.GetReleaseForPoster(first)!;
        var bytes=File.ReadAllBytes(Path.Combine(rig.PostersDirectory,donor.PosterFile!));
        var next=Movie(rig);
        using(var db=rig.Database.Open()) db.Execute("CREATE TRIGGER fail_reuse BEFORE INSERT ON poster_store_refs WHEN NEW.release_id="+next+" BEGIN SELECT RAISE(ABORT,'test'); END;");
        var result=await rig.FetchAsync(next); Assert.False(result.Ok);
        Assert.Null(rig.GetReleaseForPoster(next)!.TmdbId); Assert.Null(rig.GetReleaseForPoster(next)!.PosterFile);
        Assert.Equal(bytes,File.ReadAllBytes(Path.Combine(rig.PostersDirectory,donor.PosterFile!)));
        using var conn=rig.Database.Open(); Assert.Equal(1,conn.ExecuteScalar<int>("SELECT COUNT(*) FROM poster_store_refs"));
    }
    [Fact]
    public async Task ExplicitTvmazeRefreshBypassesTheEvidenceCacheWithoutSearchingAgain()
    {
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeHit,completeTmdbMetadata:true);
        var id=rig.CreateRelease("Show Tvmaze Hit (2015)","Show Tvmaze Hit",2015,UnifiedCategory.Serie,"series");
        Assert.True((await rig.FetchAsync(id)).Ok);
        var matching=rig.TvmazeMatchingRequests;
        var before=rig.GetReleaseForPoster(id)!;
        Assert.True((await rig.Posters.FetchPosterAsync(id,default,false,skipIfExists:false)).Ok);
        Assert.Equal(matching+1,rig.TvmazeMatchingRequests);
        Assert.Equal(2,rig.TvmazeImageRequests);
        Assert.Equal(before.ExtProviderId,rig.GetReleaseForPoster(id)!.ExtProviderId);
    }

}
