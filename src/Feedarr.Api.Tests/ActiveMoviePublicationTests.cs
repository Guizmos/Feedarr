using System.Net;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Models;

namespace Feedarr.Api.Tests;

public sealed partial class ActiveMovieMatchingTests
{
    private static void AssertUnpublished(PosterMatchingContractTestRig rig, long id)
    {
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Null(row.TmdbId); Assert.Null(row.ExtProviderId); Assert.Null(row.ExtTitle); Assert.Null(row.PosterFile);
        using var conn = rig.Database.Open();
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM media_entities WHERE tmdb_id IS NOT NULL OR ext_provider_id IS NOT NULL OR poster_file IS NOT NULL"));
        if (Directory.Exists(rig.PostersDirectory))
            Assert.Empty(Directory.EnumerateFiles(rig.PostersDirectory, "*-v2-*", SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData("poster_file")]
    [InlineData("ext_title")]
    public async Task SqliteFailureRollsBackIdentityPosterAndMetadataTogether(string column)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true);
        var id = Movie(rig);
        using (var conn = rig.Database.Open())
            conn.Execute($"CREATE TRIGGER fail_publication BEFORE UPDATE OF {column} ON releases WHEN NEW.{column} IS NOT NULL BEGIN SELECT RAISE(ABORT,'injected publication failure'); END");
        Assert.False((await rig.FetchAsync(id)).Ok);
        AssertUnpublished(rig, id);
    }

    [Theory]
    [InlineData("details")]
    [InlineData("credits")]
    [InlineData("credits_not_found")]
    [InlineData("images")]
    [InlineData("download")]
    public async Task MandatoryProviderFailureLeavesNoAssociation(string phase)
    {
        var downloads = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true,
            tmdbResponse: (r, _) =>
            {
                var path = r.RequestUri!.AbsolutePath;
                var image = r.RequestUri.Host == "image.tmdb.org";
                if (image) downloads++;
                var fail = phase switch { "details" => path.EndsWith("/movie/100"), "credits" or "credits_not_found" => path.EndsWith("/credits"), "images" => path.EndsWith("/images"), _ => image };
                return Task.FromResult<HttpResponseMessage?>(fail ? new(phase == "credits_not_found" ? HttpStatusCode.NotFound : HttpStatusCode.ServiceUnavailable) : null);
            });
        var id = Movie(rig);
        Assert.False((await rig.FetchAsync(id)).Ok);
        AssertUnpublished(rig, id);
        Assert.Equal(phase == "download" ? 1 : 0, downloads);
    }

    [Theory]
    [InlineData("details")]
    [InlineData("credits")]
    [InlineData("images")]
    public async Task ResponseOwnedByAnotherIdentityCannotSupplyPublicationData(string phase)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true,
            tmdbResponse: (r, _) =>
            {
                var path = r.RequestUri!.AbsolutePath;
                var fail = phase switch { "details" => path.EndsWith("/movie/100"), "credits" => path.EndsWith("/credits"), _ => path.EndsWith("/images") };
                return Task.FromResult<HttpResponseMessage?>(fail ? Json(new { id = 999, title = "Other work", overview = "Other overview", posters = new[] { new { file_path = "/other.jpg" } } }) : null);
            });
        var id = Movie(rig);
        Assert.False((await rig.FetchAsync(id)).Ok);
        AssertUnpublished(rig, id);
    }

    [Fact]
    public async Task WrongMediaTypeInDetailsCannotSupplyMetadata()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true,
            tmdbResponse: (r, _) => Task.FromResult<HttpResponseMessage?>(r.RequestUri!.AbsolutePath.EndsWith("/movie/100")
                ? Json(new { id = 100, media_type = "tv", title = "Other series" }) : null));
        var id = Movie(rig);
        Assert.False((await rig.FetchAsync(id)).Ok);
        AssertUnpublished(rig, id);
    }

    [Fact]
    public async Task SparseVerifiedMetadataDoesNotRetainOldOrphanFields()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true,
            tmdbResponse: (r, _) => Task.FromResult<HttpResponseMessage?>(r.RequestUri!.AbsolutePath.EndsWith("/movie/100")
                ? Json(new { id = 100, title = "The Matrix" }) : null));
        var id = Movie(rig);
        using (var conn = rig.Database.Open()) conn.Execute("UPDATE releases SET ext_overview='old orphan overview',ext_tagline='old tagline' WHERE id=@id", new { id });
        Assert.True((await rig.FetchAsync(id)).Ok);
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Equal("The Matrix", row.ExtTitle); Assert.Null(row.ExtOverview); Assert.Null(row.ExtTagline);
        Assert.Equal(100, row.TmdbId); Assert.Equal("100", row.ExtProviderId);
    }

    [Fact]
    public async Task FileWriteFailureLeavesNoIdentity()
    {
        PosterMatchingContractTestRig? rig = null;
        using (rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true,
            tmdbResponse: (r, _) =>
            {
                if (r.RequestUri!.Host == "image.tmdb.org")
                {
                    Directory.Delete(rig!.PostersDirectory, recursive: true);
                    File.WriteAllText(rig.PostersDirectory, "blocked directory");
                }
                return Task.FromResult<HttpResponseMessage?>(null);
            }))
        {
            var id = Movie(rig);
            Assert.False((await rig.FetchAsync(id)).Ok);
            AssertUnpublished(rig, id);
        }
    }

    [Fact]
    public async Task DownloadDoesNotReplaceExistingSharedFile()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true);
        Directory.CreateDirectory(rig.PostersDirectory);
        var previous = Path.Combine(rig.PostersDirectory, "tmdb-movie-100-w500.jpg");
        File.WriteAllText(previous, "existing bytes unchanged");
        var id = Movie(rig);
        Assert.True((await rig.FetchAsync(id)).Ok);
        Assert.Equal("existing bytes unchanged", File.ReadAllText(previous));
        var row = rig.GetReleaseForPoster(id)!;
        Assert.NotEqual(Path.GetFileName(previous), row.PosterFile);
        Assert.Equal(File.ReadAllBytes(Path.Combine(rig.PostersDirectory, row.PosterFile!)),
            File.ReadAllBytes(Path.Combine(rig.Posters.PosterStoreDirPath, row.PosterStoreDir!, "original.jpg")));
    }

    [Fact]
    public async Task DerivedStoreFailureRetainsCompleteUsablePublicationAcrossRestart()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true);
        Directory.CreateDirectory(rig.PostersDirectory);
        File.WriteAllText(Path.Combine(rig.PostersDirectory, "store"), "blocked store");
        var id = Movie(rig);
        var result = await rig.FetchAsync(id);
        Assert.True(result.Ok);
        Assert.True(JsonSerializer.SerializeToElement(result.Body).GetProperty("storePending").GetBoolean());
        var published = rig.GetReleaseForPoster(id)!;
        Assert.Equal(100, published.TmdbId); Assert.Equal("100", published.ExtProviderId); Assert.Equal("Mock", published.ExtTitle);
        Assert.True(File.Exists(Path.Combine(rig.PostersDirectory, published.PosterFile!)));
        var searches = rig.TmdbSearchRequests.Count;
        rig.RestartPosters(_ => { });
        var refreshed = await rig.Posters.FetchPosterAsync(id, default, false, skipIfExists: false);
        Assert.True(refreshed.Ok);
        Assert.True(JsonSerializer.SerializeToElement(refreshed.Body).GetProperty("storePending").GetBoolean());
        Assert.Equal(searches, rig.TmdbSearchRequests.Count);
        var after = rig.GetReleaseForPoster(id)!;
        Assert.Equal(published.TmdbId, after.TmdbId);
        Assert.Equal(published.TmdbMediaType, after.TmdbMediaType);
        Assert.Equal(published.MatchRevision, after.MatchRevision);
        Assert.Equal(published.ExtProviderId, after.ExtProviderId);
        Assert.Equal(published.ExtTitle, after.ExtTitle);
        Assert.NotEqual(published.PosterFile, after.PosterFile);
        Assert.Equal(File.ReadAllBytes(Path.Combine(rig.PostersDirectory, published.PosterFile!)),
            File.ReadAllBytes(Path.Combine(rig.PostersDirectory, after.PosterFile!)));
    }

    [Fact]
    public async Task LateManualCorrectionAtDownloadPreventsStaleFileAndDatabasePublication()
    {
        PosterMatchingContractTestRig? rig = null;
        long id = 0;
        using (rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true,
            tmdbResponse: (r, _) =>
            {
                if (r.RequestUri!.Host == "image.tmdb.org") rig!.Repository.SetManualMatch(id, "tmdb", "movie", "999");
                return Task.FromResult<HttpResponseMessage?>(null);
            }))
        {
            id = Movie(rig);
            var result = await rig.FetchAsync(id);
            Assert.False(result.Ok); Assert.Equal(409, result.StatusCode);
            var row = rig.GetReleaseForPoster(id)!;
            Assert.Equal("999", row.ManualMatchId); Assert.NotEqual(100, row.TmdbId); Assert.Null(row.PosterFile);
            Assert.Empty(Directory.GetFiles(rig.PostersDirectory, "*-v2-*"));
        }
    }

    [Fact]
    public async Task CancellationDuringDownloadLeavesNoPublication()
    {
        using var stopping = new CancellationTokenSource();
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true,
            tmdbResponse: (r, _) =>
            {
                if (r.RequestUri!.Host == "image.tmdb.org") stopping.Cancel();
                return Task.FromResult<HttpResponseMessage?>(null);
            });
        var id = Movie(rig);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Posters.FetchPosterAsync(id, stopping.Token, false));
        AssertUnpublished(rig, id);
    }

    [Fact]
    public async Task ExistingUnidentifiedMoviesUseV2AfterRestart()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true);
        var existing = Movie(rig);
        rig.RestartPosters(_ => { });
        Assert.True((await rig.FetchAsync(existing)).Ok);
        Assert.All(rig.TmdbSearchRequests, request => Assert.Contains("page=", request.Query));
        var previousCount = rig.TmdbSearchRequests.Count;
        var fresh = Movie(rig, "A.New.Movie.1999.1080p.BluRay");
        await rig.FetchAsync(fresh);
        Assert.Contains(rig.TmdbSearchRequests.Skip(previousCount), request => request.Query.Contains("page="));
    }

    [Fact]
    public async Task RestartPreservesV2AbstentionWithoutHistoricalFallback()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true);
        var pending = Movie(rig, "Wrong.Movie.1999.1080p.BluRay");
        Assert.False((await rig.FetchAsync(pending)).Ok);
        rig.RestartPosters(_ => { });
        Assert.False((await rig.FetchAsync(pending)).Ok);
        Assert.Null(rig.GetReleaseForPoster(pending)!.TmdbId);
        Assert.All(rig.TmdbSearchRequests, request => Assert.Contains("page=", request.Query));

    }
}
