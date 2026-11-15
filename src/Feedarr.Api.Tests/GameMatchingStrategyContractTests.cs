using System.Text.Json;
using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Tests;

public sealed class GameMatchingStrategyContractTests
{
    [Theory]
    [InlineData("GameIgdbHit", "igdb", "901")]
    [InlineData("GameRawgHit", "rawg", "123")]
    public async Task Game_PublishesIdentityMetadataAndPoster_AndPreservesItOnRepeat(string scenario, string provider, string providerId)
    {
        using var rig = new PosterMatchingContractTestRig(Enum.Parse<PosterContractScenario>(scenario));
        var id = rig.CreateRelease("Game Hit", "Game Hit", 2022, UnifiedCategory.JeuWindows, "game");
        var result = await rig.FetchSnapshotAsync(id);
        Assert.True(result.Ok);
        Assert.Equal(provider, result.PosterProvider);
        Assert.StartsWith($"{provider}-{providerId}-v2-", result.PosterFile);
        Assert.Null(result.TmdbId);
        Assert.Null(result.MatchSource); // A historic first-hit cache cannot make a new V2 decision.
        var state = rig.GetReleaseForPoster(id)!;
        Assert.Equal(provider, state.ExtProvider);
        Assert.Equal(providerId, state.ExtProviderId);
        var repeat = await rig.FetchSnapshotAsync(id);
        Assert.True(repeat.Ok);
        Assert.Equal(result.PosterFile, repeat.PosterFile);
        Assert.Single(provider == "igdb" ? rig.IgdbSearchRequests.Select(x => x.ToString()) : rig.RawgSearchRequests.Select(x => x.ToString()));
    }

    [Fact]
    public async Task Game_MissAbstainsWithoutWritingIdentityOrPoster()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbMiss);
        var id = rig.CreateRelease("Game Miss", "Game Miss", 2022, UnifiedCategory.JeuWindows, "game");
        var result = await rig.FetchSnapshotAsync(id);
        Assert.False(result.Ok); Assert.Equal(422, result.StatusCode);
        Assert.Null(result.PosterFile); Assert.Null(rig.GetReleaseForPoster(id)!.ExtProviderId);
    }

    [Theory]
    [InlineData("Half-Life", "[{\"id\":1,\"name\":\"Half-Life\",\"platforms\":[6]}]", "Accept")]
    [InlineData("Resident Evil (1996)", "[{\"id\":1,\"name\":\"Resident Evil\",\"platforms\":[6],\"first_release_date\":820454400},{\"id\":2,\"name\":\"Resident Evil\",\"platforms\":[6],\"first_release_date\":1009843200,\"game_type\":{\"type\":\"Remake\"}}]", "Accept")]
    [InlineData("Resident Evil", "[{\"id\":1,\"name\":\"Resident Evil\",\"platforms\":[6],\"remakes\":[2]},{\"id\":2,\"name\":\"Resident Evil\",\"platforms\":[6],\"game_type\":{\"type\":\"Remake\"}}]", "Ambiguous")]
    [InlineData("Control Deluxe Edition", "[{\"id\":1,\"name\":\"Control\",\"platforms\":[6]}]", "Reject")]
    [InlineData("Half-Life 2", "[{\"id\":1,\"name\":\"Half-Life 3\",\"platforms\":[6]}]", "Reject")]
    [InlineData("Half-Life", "[{\"id\":1,\"name\":\"Half-Life\",\"platforms\":[48]}]", "Reject")]
    [InlineData("Half-Life", "[{\"id\":1,\"name\":\"Half-Life\"}]", "Ambiguous")]
    public void Game_RanksPcEditionsRemakesAndSeriesNumbers(string title, string json, string expected)
    {
        using var doc = JsonDocument.Parse(json);
        var candidates = GameCandidateAdapter.Project("igdb", doc.RootElement);
        var result = SearchCandidateRanker.Rank(GameCandidateAdapter.Context(title), candidates.Select(r => r.Candidate).ToArray());
        Assert.Equal(expected, result.Decision.ToString());
        if (result.Decision == CandidateDecision.Accept) Assert.Equal("1", result.SelectedIdentity!.Id);
    }

    [Fact]
    public void Game_EditionEvidenceDiscriminatesAndNeverSelectsTheFirstHitBlindly()
    {
        using var doc = JsonDocument.Parse("""
            [{"id":2,"name":"Control","platforms":[6]},
             {"id":1,"name":"Control","platforms":[6],"version_title":"Deluxe","version_parent":2}]
            """);
        var candidates = GameCandidateAdapter.Project("igdb", doc.RootElement);
        var ranked = SearchCandidateRanker.Rank(GameCandidateAdapter.Context("Control.Deluxe.Edition-Repack"), candidates.Select(r => r.Candidate).ToArray());
        Assert.Equal(CandidateDecision.Accept, ranked.Decision);
        Assert.Equal("1", ranked.SelectedIdentity!.Id);
    }

    [Fact]
    public async Task Game_SharedEntityWithAnotherEditionIsNotOverwritten()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit);
        var id = rig.IngestRelease("Game Hit", UnifiedCategory.JeuWindows);
        var other = rig.IngestRelease("Game Hit Deluxe Edition", UnifiedCategory.JeuWindows);
        using (var conn = rig.Database.Open())
            conn.Execute("UPDATE releases SET entity_id=(SELECT entity_id FROM releases WHERE id=@id) WHERE id=@other", new { id, other });
        Assert.Equal(422, (await rig.FetchAsync(id)).StatusCode);
        Assert.Null(rig.GetReleaseForPoster(id)!.ExtProviderId);
        Assert.Null(rig.GetReleaseForPoster(other)!.ExtProviderId);
        Assert.Null(rig.GetReleaseForPoster(other)!.PosterFile);
    }

    [Fact]
    public void Game_IncompleteProviderPagesCannotHideRivals()
    {
        using var rawg = JsonDocument.Parse("{\"count\":26,\"next\":\"https://untrusted.invalid\",\"results\":[]}");
        Assert.Throws<InvalidDataException>(() => GameCandidateAdapter.Project("rawg", rawg.RootElement));
        using var igdb = JsonDocument.Parse("[" + string.Join(",", Enumerable.Repeat("{}", 26)) + "]");
        Assert.Throws<InvalidDataException>(() => GameCandidateAdapter.Project("igdb", igdb.RootElement));
    }

    [Fact]
    public async Task Game_RevisionChangedDuringDownload_DiscardsPublication()
    {
        PosterMatchingContractTestRig? current = null;
        long currentId = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit, igdbResponse: (request, _) =>
        {
            if (request.RequestUri!.Host == "images.igdb.com") current!.Repository.SetManualMatch(currentId, "igdb", "game", "902");
            return Task.FromResult<HttpResponseMessage?>(null);
        });
        current = rig;
        currentId = rig.CreateRelease("Game Hit", "Game Hit", 2022, UnifiedCategory.JeuWindows, "game");
        var result = await rig.FetchAsync(currentId);
        Assert.Equal(409, result.StatusCode);
        var saved = rig.GetReleaseForPoster(currentId)!;
        Assert.Equal("902", saved.ManualMatchId);
        Assert.Null(saved.PosterFile);
    }

    [Fact]
    public async Task Game_PublicationFailureRollsBackIdentityAndMetadata()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit);
        var id = rig.CreateRelease("Game Hit", "Game Hit", 2022, UnifiedCategory.JeuWindows, "game");
        using (var conn = rig.Database.Open()) conn.Execute("CREATE TRIGGER fail_game_details BEFORE UPDATE OF ext_title ON releases WHEN NEW.ext_title IS NOT NULL BEGIN SELECT RAISE(ABORT,'test rollback'); END;");
        var result = await rig.FetchAsync(id);
        Assert.False(result.Ok);
        var saved = rig.GetReleaseForPoster(id)!;
        Assert.Null(saved.ExtProviderId); Assert.Null(saved.PosterFile); Assert.Null(saved.ExtTitle);
    }

    [Fact]
    public async Task Game_ManualTitleIsPreservedWithoutProviderSearch()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit);
        var id = rig.CreateRelease("Game Hit", "Game Hit", 2022, UnifiedCategory.JeuWindows, "game");
        rig.SetManualTitleForTest(id);
        Assert.Equal(409, (await rig.FetchAsync(id)).StatusCode);
        Assert.Empty(rig.IgdbSearchRequests);
        Assert.Null(rig.GetReleaseForPoster(id)!.PosterFile);
    }
}
