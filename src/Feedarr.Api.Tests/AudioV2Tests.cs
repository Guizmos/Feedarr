using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Tests;

public sealed class AudioV2Tests
{
    private const string ReleaseId = "11111111-1111-4111-8111-111111111111";
    private const string GroupId = "22222222-2222-4222-8222-222222222222";
    private static JsonElement Mb(params string[] ids) => JsonSerializer.SerializeToElement(new {
        count = ids.Length, releases = ids.Select(id => new Dictionary<string, object> {
            ["id"] = id, ["title"] = "Around the World", ["date"] = "1997-01-01",
            ["artist-credit"] = new[] { new { name = "Daft Punk", artist = new { id = "33333333-3333-4333-8333-333333333333", name = "Daft Punk" } } },
            ["release-group"] = new Dictionary<string, object> { ["id"] = GroupId, ["primary-type"] = "Album" }
        }) });
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static Task<HttpResponseMessage?> MusicResponse(HttpRequestMessage request, CancellationToken _)
    {
        var uri = request.RequestUri!;
        if (uri.Host == "musicbrainz.org") return Task.FromResult<HttpResponseMessage?>(Json(Mb(ReleaseId).GetRawText()));
        if (uri.Host == "coverartarchive.org") return Task.FromResult<HttpResponseMessage?>(Json("""{"images":[{"front":true,"image":"https://cover.test/front.jpg"}]}"""));
        return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK) { Content = new ByteArrayContent(PosterTestImage.Create()) });
    }
    private static CandidateRankingResult Rank(ProviderSearchCandidate row, string artist = "Daft Punk", string? kind = null, string? edition = null) =>
        SearchCandidateRanker.Rank(new(new[] { new SearchCandidate("Around the World", null, CandidateYearEvidence.None, SearchCandidateOrigin.Primary) },
            UnifiedCategory.Audio, SearchMediaType.Audio, AudioArtist: artist, AudioKind: kind, AudioEdition: edition), new[] { row });

    [Theory]
    [InlineData("Daft Punk", "album", null, "Accept")]
    [InlineData("Another artist", "album", null, "Reject")]
    [InlineData(null, "album", null, "Ambiguous")]
    [InlineData("Daft Punk", null, null, "Ambiguous")]
    [InlineData("Daft Punk", "special", null, "Ambiguous")]
    [InlineData("Daft Punk", "album", "remaster", "Reject")]
    public void ArtistTypeAndEditionAreEvidence_NotAssumptions(string? artist, string? kind, string? edition, string expected)
    {
        var id = new SearchIdentity("musicbrainz", SearchMediaType.Audio, ReleaseId);
        var row = new ProviderSearchCandidate(id, "Around the World", Audio: new(id, artist, kind, edition));
        Assert.Equal(expected, Rank(row).Decision.ToString());
    }

    [Fact]
    public void MusicBrainzKeepsReleaseAndGroupIdentitiesSeparate_AndCompetingEditionsAmbiguous()
    {
        var rows = AudioCandidateAdapter.FromMusicBrainz(Mb(ReleaseId, "44444444-4444-4444-8444-444444444444"));
        Assert.All(rows, r => { Assert.NotEqual(GroupId, r.Candidate.Identity.Id); Assert.Equal(GroupId, r.Candidate.Audio!.ReleaseGroup!.Id);
            Assert.Equal("musicbrainz-release-group", r.Candidate.Audio.ReleaseGroup.Provider);
            Assert.Equal("musicbrainz-artist", r.Candidate.Audio.Artists!.Single().Provider);
            Assert.NotEqual(r.Candidate.Identity.Id, r.Candidate.Audio.Artists!.Single().Id); });
        var context = AudioCandidateAdapter.Context("Daft Punk - Around the World", "Daft Punk", "Around the World");
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(context, rows.Select(r => r.Candidate).ToArray()).Decision);
    }

    [Fact]
    public void AudioDbAlbumEndpointWithoutReleaseFormatIsNotProofOfStudioAlbum()
    {
        using var data = JsonDocument.Parse("""{"album":[{"idAlbum":"5500","strAlbum":"Around the World","strArtist":"Daft Punk"}]}""");
        var candidate = AudioCandidateAdapter.FromAudioDb(data.RootElement, "album").Single().Candidate;
        Assert.Equal("album:5500", candidate.Identity.Id);
        Assert.Equal(CandidateDecision.Ambiguous, Rank(candidate).Decision);
    }

    [Fact]
    public void IncompleteMusicBrainzSearchCannotHideCompetingReleases()
    {
        using var document = JsonDocument.Parse("""{"count":27,"releases":[]}""");
        Assert.Throws<InvalidDataException>(() => AudioCandidateAdapter.FromMusicBrainz(document.RootElement));
    }

    [Fact]
    public async Task TrackAndAlbumWithTheSameTitleAbstainWithoutPublishing()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.AudioAudioDbHit);
        var id = rig.CreateRelease("Daft Punk - Around the World", "Around the World", 1997, UnifiedCategory.Audio, "audio");
        Assert.Equal(422, (await rig.FetchAsync(id)).StatusCode);
        Assert.Null(rig.GetReleaseForPoster(id)!.ExtProviderId);
        Assert.Null(rig.GetReleaseForPoster(id)!.PosterFile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedIdentityPublishesAtomically_AndRefreshesWithoutNewSearch(bool musicBrainz)
    {
        var searches = 0;
        using var rig = new PosterMatchingContractTestRig(musicBrainz ? PosterContractScenario.GameIgdbMiss : PosterContractScenario.AudioAudioDbHit,
            audioResponse: (request, _) => { if (request.RequestUri!.AbsolutePath.Contains("search")) searches++; return Task.FromResult<HttpResponseMessage?>(null); },
            musicBrainzResponse: musicBrainz ? (request, token) => { if (request.RequestUri!.Host == "musicbrainz.org") searches++; return MusicResponse(request, token); } : null);
        var raw = "Daft Punk - Around the World" + (musicBrainz ? " [Album]" : " [Track]");
        var id = rig.CreateRelease(raw, raw, 1997, UnifiedCategory.Audio, "audio");
        Assert.True((await rig.FetchAsync(id)).Ok);
        var before = rig.GetReleaseForPoster(id)!;
        Assert.Equal(musicBrainz ? "musicbrainz" : "theaudiodb", before.ExtProvider);
        Assert.Equal(musicBrainz ? ReleaseId : "track:4500", before.ExtProviderId);
        Assert.Equal("Around the World", before.ExtTitle);
        Assert.NotNull(before.PosterFile); Assert.Null(before.TmdbId);
        var count = searches;
        var refreshed = await rig.Posters.FetchPosterAsync(id, default, false, skipIfExists: false);
        Assert.True(refreshed.Ok);
        var after = rig.GetReleaseForPoster(id)!;
        Assert.Equal(before.ExtProviderId, after.ExtProviderId);
        Assert.Equal(before.ExtTitle, after.ExtTitle);
        Assert.NotEqual(before.PosterFile, after.PosterFile);
        Assert.Equal(count, searches);
    }

    [Fact]
    public async Task ManualChoiceIsPreservedWithoutSearching()
    {
        var searches = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.AudioAudioDbHit, audioResponse: (_, _) => { searches++; return Task.FromResult<HttpResponseMessage?>(null); });
        var id = rig.CreateRelease("Daft Punk - Around the World", "Around the World", 1997, UnifiedCategory.Audio, "audio");
        rig.Repository.SetManualMatch(id, "musicbrainz", "audio", ReleaseId);
        Assert.True((await rig.FetchAsync(id)).Ok);
        Assert.Equal(ReleaseId, rig.GetReleaseForPoster(id)!.ManualMatchId);
        Assert.Equal(0, searches);
    }

    [Fact]
    public async Task ManualChoiceDuringImageDownloadRejectsStalePublication()
    {
        PosterMatchingContractTestRig? active = null; long id = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.AudioAudioDbHit, audioResponse: (request, _) => {
            if (request.RequestUri!.AbsolutePath.EndsWith(".jpg")) active!.Repository.SetManualMatch(id, "musicbrainz", "audio", ReleaseId);
            return Task.FromResult<HttpResponseMessage?>(null);
        });
        active = rig;
        id = rig.CreateRelease("Daft Punk - Around the World [Track]", "Around the World", 1997, UnifiedCategory.Audio, "audio");
        Assert.Equal(409, (await rig.FetchAsync(id)).StatusCode);
        Assert.Equal(ReleaseId, rig.GetReleaseForPoster(id)!.ManualMatchId);
        Assert.Null(rig.GetReleaseForPoster(id)!.PosterFile);
    }

    [Fact]
    public async Task MetadataFailureRollsBackIdentityAndPoster()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.AudioAudioDbHit);
        var id = rig.CreateRelease("Daft Punk - Around the World [Track]", "Around the World", 1997, UnifiedCategory.Audio, "audio");
        using (var conn = rig.Database.Open()) conn.Execute("CREATE TRIGGER fail_audio BEFORE UPDATE OF ext_title ON releases WHEN NEW.ext_title IS NOT NULL BEGIN SELECT RAISE(ABORT,'rollback'); END;");
        Assert.False((await rig.FetchAsync(id)).Ok);
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Null(row.ExtProviderId); Assert.Null(row.ExtTitle); Assert.Null(row.PosterFile);
    }

    [Fact]
    public async Task CancellationDuringImageDownloadPublishesNothing()
    {
        using var cancel = new CancellationTokenSource();
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.AudioAudioDbHit, audioResponse: async (request, token) => {
            if (request.RequestUri!.AbsolutePath.EndsWith(".jpg")) { cancel.Cancel(); await Task.Delay(Timeout.Infinite, token); }
            return null;
        });
        var id = rig.CreateRelease("Daft Punk - Around the World [Track]", "Around the World", 1997, UnifiedCategory.Audio, "audio");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.FetchAsync(id, cancel.Token));
        Assert.Null(rig.GetReleaseForPoster(id)!.ExtProviderId);
        Assert.Null(rig.GetReleaseForPoster(id)!.PosterFile);
    }

    [Fact]
    public async Task ArtistMissingAbstainsWithoutNetwork()
    {
        var requests = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.AudioAudioDbHit, audioResponse: (_, _) => { requests++; return Task.FromResult<HttpResponseMessage?>(null); });
        var id = rig.CreateRelease("Around the World", "Around the World", 1997, UnifiedCategory.Audio, "audio");
        Assert.Equal(422, (await rig.FetchAsync(id)).StatusCode);
        Assert.Equal(0, requests);
    }
}
