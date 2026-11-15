using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Tests;

public sealed class TelevisionMatchingTests
{
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrForeignEpisodeEvidenceCannotMakeAnExactTitleAccept(bool foreign)
    {
        var identity = new SearchIdentity("tmdb", SearchMediaType.Series, "1");
        var queries = new[] { new SearchCandidate("FBI", null, CandidateYearEvidence.None, SearchCandidateOrigin.Primary) };
        var candidate = new ProviderSearchCandidate(identity, "FBI", 2018, Season: 8, Episode: 5,
            Television: foreign ? new(new("tmdb", SearchMediaType.Series, "2"), true, true) : null);
        var context = new CandidateRankingContext(queries, UnifiedCategory.Serie, SearchMediaType.Series, Season: 8, Episode: 5);
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(context, new[] { candidate }).Decision);
        var proven = candidate with { Television = new(identity, true, true) };
        Assert.Equal(CandidateDecision.Accept, SearchCandidateRanker.Rank(context, new[] { proven }).Decision);
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(context,
            new[] { proven, proven with { Identity = new("tmdb", SearchMediaType.Series, "2"), Television = new(new("tmdb", SearchMediaType.Series, "2"), true, true) } }).Decision);
    }

    [Fact]
    public void BroadcastDateIsNotTheSeriesCreationYear()
    {
        var id = new SearchIdentity("tmdb", SearchMediaType.Series, "1");
        var query = new SearchCandidate("A Programme", 2026, CandidateYearEvidence.Parsed, SearchCandidateOrigin.Primary);
        var context = new CandidateRankingContext(new[] { query }, UnifiedCategory.Emission, SearchMediaType.Series, AirDate: "2026-11-14");
        var candidate = new ProviderSearchCandidate(id, "A Programme", 2001, Television: new(id, null, null, "2026-11-14"));
        Assert.Equal(CandidateDecision.Accept, SearchCandidateRanker.Rank(context, new[] { candidate }).Decision);
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(context, new[] { candidate with { Television = new(id, null, null, "2026-11-13") } }).Decision);
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(context, new[] { candidate with { Television = null } }).Decision);
    }

    [Theory]
    [InlineData(UnifiedCategory.Serie, false)]
    [InlineData(UnifiedCategory.Serie, true)]
    [InlineData(UnifiedCategory.Emission, false)]
    [InlineData(UnifiedCategory.Emission, true)]
    public async Task ManualAndEstablishedIdentitiesRemainUntouched(UnifiedCategory category, bool manual)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeMissTmdb, completeTmdbMetadata: true);
        var id = rig.IngestRelease("Show.Tmdb.Hit.S01.1080p.WEB.x264-GRP", category);
        if (manual) rig.SetManualTitleForTest(id);
        else { using var db = rig.Database.Open(); db.Execute("UPDATE media_entities SET tmdb_id=999,tmdb_media_type='tv',ext_overview='keep' WHERE id=(SELECT entity_id FROM releases WHERE id=@id)", new { id }); }
        await rig.FetchAsync(id);
        Assert.Empty(rig.TmdbSearchRequests);
        if (!manual) { Assert.Equal(999, rig.GetReleaseForPoster(id)!.TmdbId); Assert.Equal("keep", rig.GetReleaseForPoster(id)!.ExtOverview); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityChangesAndPublicationErrorsNeverLeavePartialMetadata(bool rollback)
    {
        PosterMatchingContractTestRig? current = null; long id = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeMissTmdb, completeTmdbMetadata: true,
            tmdbResponse: (request, _) => {
                if (!rollback && request.RequestUri!.AbsolutePath.EndsWith("/images")) current!.SetManualTitleForTest(id);
                return Task.FromResult<HttpResponseMessage?>(null);
            });
        current = rig; id = rig.IngestRelease("Show.Tmdb.Hit.S01.1080p.WEB.x264-GRP", UnifiedCategory.Serie);
        if (rollback) { using var db = rig.Database.Open(); db.Execute("CREATE TRIGGER fail_tv_publication BEFORE UPDATE OF ext_provider ON media_entities BEGIN SELECT RAISE(ABORT, 'test'); END"); }
        var result = await rig.FetchAsync(id);
        Assert.False(result.Ok); Assert.Equal(rollback ? 503 : 409, result.StatusCode);
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Null(row.TmdbId); Assert.Null(row.ExtProviderId); Assert.Null(row.PosterFile);
    }
}
