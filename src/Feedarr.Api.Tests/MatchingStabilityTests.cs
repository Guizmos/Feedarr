using System.Net;
using System.Text;
using Dapper;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Torznab;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class MatchingStabilityTests
{
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData("MovieTmdbHit", UnifiedCategory.Film, "The.Matrix.1999.1080p.BluRay.x264-GRP")]
    [InlineData("SeriesTvmazeHit", UnifiedCategory.Serie, "Show.Tvmaze.Hit.S01E02.1080p.WEB.x264-GRP")]
    [InlineData("GameIgdbHit", UnifiedCategory.JeuWindows, "Game Hit")]
    [InlineData("AnimeJikanHit", UnifiedCategory.Anime, "Naruto.2002.1080p.BluRay.x264-GRP")]
    public async Task EstablishedIdentityCanRefreshAndRecoverAMissingFileWithoutSearching(string scenario, UnifiedCategory category, string title)
    {
        using var rig = new PosterMatchingContractTestRig(Enum.Parse<PosterContractScenario>(scenario), completeTmdbMetadata: true);
        var id = rig.IngestRelease(title, category);
        Assert.True((await rig.FetchAsync(id)).Ok);
        var initial = rig.GetReleaseForPoster(id)!;
        var calls = (rig.TmdbSearchRequests.Count, rig.IgdbSearchRequests.Count);
        Assert.True((await rig.Posters.FetchPosterAsync(id, default, false, skipIfExists: false)).Ok);
        var refreshed = rig.GetReleaseForPoster(id)!;
        Assert.Equal(initial.TmdbId, refreshed.TmdbId); Assert.Equal(initial.ExtProviderId, refreshed.ExtProviderId);
        Assert.NotEqual(initial.PosterFile, refreshed.PosterFile);
        File.Delete(Path.Combine(rig.Posters.PostersDirPath, refreshed.PosterFile!));
        Assert.True((await rig.FetchAsync(id)).Ok);
        var recovered = rig.GetReleaseForPoster(id)!;
        Assert.Equal(initial.ExtProviderId, recovered.ExtProviderId);
        Assert.True(File.Exists(Path.Combine(rig.Posters.PostersDirPath, recovered.PosterFile!)));
        Assert.Equal(calls, (rig.TmdbSearchRequests.Count, rig.IgdbSearchRequests.Count));
    }

    [Fact]
    public void RssRoutingChangesInvalidateRevisionButCountersDoNotReassociateIdentity()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        void Sync(int category, int seeders) => rig.Repository.UpsertMany(1, "TEST", new[] {
            new TorznabItem { Guid = "routing", Title = "The.Matrix.1999.1080p.BluRay.x264-GRP", CategoryId = category,
                CategoryIds = new() { category }, Seeders = seeders, PublishedAtTs = 1000 + seeders }
        }, categoryMap: new() { [2000] = ("films", "Films"), [2040] = ("films", "Films") });
        Sync(2000, 1);
        long id;
        using (var db = rig.Database.Open()) id = db.QuerySingle<long>("SELECT id FROM releases WHERE guid='routing'");
        var before = rig.GetReleaseForPoster(id)!;
        using (rig.Repository.BeginMatching(before)) {
            Sync(2040, 2);
            Assert.Throws<ReleaseRepository.StaleMatchException>(() => rig.Repository.ValidateMatching(id));
        }
        var routed = rig.GetReleaseForPoster(id)!;
        Assert.Equal(before.EntityId, routed.EntityId);
        Assert.NotEqual(before.MatchRevision, routed.MatchRevision);
        using (rig.Repository.BeginMatching(routed)) {
            Sync(2040, 100);
            rig.Repository.ValidateMatching(id);
        }
        Assert.Equal(routed.MatchRevision, rig.GetReleaseForPoster(id)!.MatchRevision);
    }

    [Fact]
    public async Task RoutingEvidenceChangedDuringDownloadCannotPublish()
    {
        PosterMatchingContractTestRig? active = null; long id = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true, tmdbResponse: (request, _) => {
            if (request.RequestUri!.Host == "image.tmdb.org") {
                using var db = active!.Database.Open();
                db.Execute("UPDATE releases SET category_ids='5040',std_category_id=5040 WHERE id=@id", new { id });
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        });
        active = rig;
        id = rig.IngestRelease("The.Matrix.1999.1080p.BluRay.x264-GRP", UnifiedCategory.Animation);
        Assert.Equal(409, (await rig.FetchAsync(id)).StatusCode);
        Assert.Null(rig.GetReleaseForPoster(id)!.ExtProviderId); Assert.Null(rig.GetReleaseForPoster(id)!.PosterFile);
    }

    [Fact]
    public async Task ManualAudioKeepsTrackAndAlbumWithTheSameNumberDistinct()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.AudioAudioDbHit, audioResponse: (request, _) =>
            Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.Contains("searchalbum") ? Json("""
             {"album":[{"idAlbum":"4500","strAlbum":"Around the World","strArtist":"Daft Punk","strAlbumThumb":"https://www.theaudiodb.com/images/media/album/around.jpg"}]}
             """) : null));
        var rows = await rig.AudioClient.SearchAudioListAsync("Around the World", "Daft Punk", null, default);
        Assert.Contains(rows, r => r.ProviderId == "track:4500"); Assert.Contains(rows, r => r.ProviderId == "album:4500");
        foreach (var row in rows) {
            var id = rig.CreateRelease(row.ProviderId, row.ProviderId, null, UnifiedCategory.Audio, "audio");
            Assert.NotNull(await rig.Posters.SaveTheAudioDbPosterAsync(id, row.ProviderId, row.PosterUrl!, default));
            Assert.Equal(row.ProviderId, rig.GetReleaseForPoster(id)!.ManualMatchId);
            Assert.Equal(row.ProviderId, rig.GetReleaseForPoster(id)!.ExtProviderId);
        }
    }

    [Theory]
    [InlineData("issue")]
    [InlineData("volume")]
    public async Task ManualComicPreservesResourceTypeThroughPublication(string kind)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.ComicComicVineHit, comicResponse: (request, _) =>
            Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.Contains("/search") ? Json(
                "{\"results\":[{\"resource_type\":\"" + kind + "\",\"id\":9900,\"name\":\"Batman #1\",\"image\":{\"original_url\":\"https://comicvine.gamespot.com/mock-comic.jpg\"}}]}") : null));
        var found = await rig.ComicClient.SearchComicAsync("Batman #1", null, default);
        Assert.Equal(kind + ":9900", found!.ProviderId);
        var id = rig.CreateRelease("Batman #1", "Batman #1", null, UnifiedCategory.Comic, "comic");
        Assert.NotNull(await rig.Posters.SaveComicVinePosterAsync(id, found.ProviderId, found.CoverUrl!, default));
        Assert.Equal(kind + ":9900", rig.GetReleaseForPoster(id)!.ManualMatchId);
        Assert.Equal(kind + ":9900", rig.GetReleaseForPoster(id)!.ExtProviderId);
    }

    [Fact]
    public async Task HistoricalManualNumberIsPreservedWithoutGuessingAResourceType()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.ComicComicVineHit);
        var id = rig.CreateRelease("Old", "Old", null, UnifiedCategory.Comic, "comic");
        rig.Repository.SetManualMatch(id, "comicvine", "comic", "9900");
        Assert.True((await rig.FetchAsync(id)).Ok);
        Assert.Equal("9900", rig.GetReleaseForPoster(id)!.ManualMatchId);
        Assert.False((await rig.Posters.FetchPosterAsync(id, default, false, skipIfExists: false)).Ok);
        Assert.Equal("9900", rig.GetReleaseForPoster(id)!.ManualMatchId);
    }

    [Fact]
    public async Task DuplicateAudioObservationsCannotPublishAnUnverifiedFirstRow()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.AudioAudioDbHit, audioResponse: (request, _) =>
            Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.Contains("searchtrack") ? Json("""
             {"track":[{"idTrack":"4500","strTrack":"Around the World","strTrackThumb":"https://www.theaudiodb.com/images/media/track/around.jpg"},
                       {"idTrack":"4500","strTrack":"Around the World","strArtist":"Daft Punk","strTrackThumb":"https://www.theaudiodb.com/images/media/track/around.jpg"}]}
             """) : null));
        var id = rig.CreateRelease("Daft Punk - Around the World [Track]", "Around the World", null, UnifiedCategory.Audio, "audio");
        var result = await rig.FetchAsync(id);
        Assert.Equal(422, result.StatusCode);
        Assert.Null(rig.GetReleaseForPoster(id)!.ExtProviderId); Assert.Null(rig.GetReleaseForPoster(id)!.PosterFile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterCommitDoesNotTurnPublicationOrWorkerIntoFailure(bool worker)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit);
        var id = rig.IngestRelease("Game Hit", UnifiedCategory.JeuWindows);
        var gate = rig.Posters.GetStoreLock("igdb-901"); await gate.WaitAsync();
        using var stop = new CancellationTokenSource();
        try {
            Task<PosterFetchResult>? direct = null; Task<PosterFetchProcessResult>? processed = null;
            if (worker) {
                var processor = new PosterFetchJobProcessor(NullLogger<PosterFetchJobProcessor>.Instance,
                    new PosterFetchQueue(NullLogger<PosterFetchQueue>.Instance), rig.Posters, rig.Repository, null!,
                    OptionsFactory.Create(new PosterFetchOptions { RequestTimeoutSeconds = 10, MaxItemDurationSeconds = 10, MaxAttempts = 1 }));
                processed = processor.ProcessJobAsync(new PosterFetchJob(id, "Game Hit", null, UnifiedCategory.JeuWindows, true, 0, null, null), 1, stop.Token);
            } else direct = rig.FetchAsync(id, stop.Token);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (rig.GetReleaseForPoster(id)!.ExtProviderId is null) await Task.Delay(10, timeout.Token);
            stop.Cancel();
            if (worker) Assert.True((await processed!.WaitAsync(TimeSpan.FromSeconds(3))).Succeeded);
            else { var result = await direct!.WaitAsync(TimeSpan.FromSeconds(3)); Assert.True(result.Ok); Assert.True(result.PublicationCommitted); }
            Assert.Equal("901", rig.GetReleaseForPoster(id)!.ExtProviderId);
            Assert.NotNull(rig.GetReleaseForPoster(id)!.PosterFile);
        } finally { gate.Release(); }
    }
    [Fact]
    public async Task FailedRefreshRollsBackAndKeepsThePreviouslyValidPosterAndMetadata()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true);
        var id = rig.IngestRelease("The.Matrix.1999.1080p.BluRay.x264-GRP", UnifiedCategory.Film);
        Assert.True((await rig.FetchAsync(id)).Ok);
        var before = rig.GetReleaseForPoster(id)!;
        using (var db = rig.Database.Open()) db.Execute("CREATE TRIGGER refresh_fail BEFORE UPDATE OF ext_title ON releases WHEN NEW.ext_title IS NOT NULL BEGIN SELECT RAISE(ABORT,'refresh rollback'); END;");
        Assert.False((await rig.Posters.FetchPosterAsync(id, default, false, skipIfExists: false)).Ok);
        var after = rig.GetReleaseForPoster(id)!;
        Assert.Equal(before.ExtProviderId, after.ExtProviderId);
        Assert.Equal(before.ExtOverview, after.ExtOverview);
        Assert.Equal(before.PosterFile, after.PosterFile);
        Assert.True(File.Exists(Path.Combine(rig.Posters.PostersDirPath, before.PosterFile!)));
    }

    [Fact]
    public async Task RefreshCannotClearAConflictingHistoricalIdentity()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit);
        var id = rig.CreateRelease("Legacy", "Legacy", null, UnifiedCategory.JeuWindows, "game", tmdbId: 123);
        using (var db = rig.Database.Open()) db.Execute("UPDATE releases SET ext_provider='igdb',ext_provider_id='901' WHERE id=@id", new { id });
        var result = await rig.Posters.FetchPosterAsync(id, default, false, skipIfExists: false);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal(123, rig.GetReleaseForPoster(id)!.TmdbId);
        Assert.Equal("901", rig.GetReleaseForPoster(id)!.ExtProviderId);
        Assert.Empty(rig.IgdbSearchRequests);
    }

}
