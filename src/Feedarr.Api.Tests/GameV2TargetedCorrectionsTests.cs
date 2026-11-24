using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Tests;

public sealed class GameV2TargetedCorrectionsTests
{
    private static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK) {
        Content = new StringContent(JsonSerializer.Serialize(data), System.Text.Encoding.UTF8, "application/json") };
    private static (int Offset, int Limit) Window(HttpRequestMessage request)
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        Assert.DoesNotContain("sort ", body);
        return (int.Parse(Regex.Match(body, @"offset (\d+)").Groups[1].Value), int.Parse(Regex.Match(body, @"limit (\d+)").Groups[1].Value));
    }
    private static JsonElement[] Items(int total) => Enumerable.Range(1, total)
        .Select(i => JsonSerializer.SerializeToElement(new { id = i, name = "Other " + i, platforms = new[] { 6 } })).ToArray();
    private static JsonElement Recorded(string query)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "igdb-four-searches-20261123.json")));
        return doc.RootElement.GetProperty("Responses").EnumerateArray().Single(r => r.GetProperty("Query").GetString() == query).GetProperty("Candidates").Clone();
    }

    [Theory]
    [InlineData("Phantom.Blade.Zero-voices38", "Phantom Blade Zero")]
    [InlineData("Demon.Slayer.2-voices38", "Demon Slayer 2")]
    [InlineData("F1.24.1917-voices38", "F1 24 1917")]
    [InlineData("Game.Collection.Vol.2-voices38", "Game Collection Vol 2")]
    [InlineData("Game.Bundle.Pack.2-voices38", "Game Bundle Pack 2")]
    [InlineData("Game.DLC.Content.2-voices38", "Game DLC Content 2")]
    public void IsolatedUploaderAddsOneVariantWithoutRewritingDisplayOrScope(string raw, string clean)
    {
        var queries = SearchCandidateGenerator.Generate(new(raw, UnifiedCategory.JeuWindows));
        Assert.Equal(2, queries.Count);
        Assert.Contains("voices38", queries[0].Title);
        Assert.Equal(clean, queries[1].Title);
        Assert.Equal(SearchCandidateOrigin.IsolatedDistributionSuffix, queries[1].Origin);
        Assert.Equal(queries[0].Year, queries[1].Year);
        Assert.Equal(queries[0].YearEvidence, queries[1].YearEvidence);
        Assert.Equal(queries[0].Title, new Feedarr.Api.Services.Titles.TitleParser().Parse(raw, UnifiedCategory.JeuWindows).TitleClean);
    }

    [Theory]
    [InlineData("Voices38 Game")]
    [InlineData("Game voices38")]
    [InlineData("Game-voices38-story")]
    [InlineData("Game-voices380")]
    [InlineData("voices38")]
    public void NonIsolatedOrLegitimateWordsDoNotCreateVariants(string raw)
        => Assert.Single(SearchCandidateGenerator.Generate(new(raw, UnifiedCategory.JeuWindows)));

    [Theory]
    [InlineData("The Binding of Isaac Rebirth", "Accept", "7789")]
    [InlineData("Phantom Blade Zero voices38", "Reject", null)]
    [InlineData("Phantom Blade Zero", "Accept", "250618")]
    public void CompleteRecordedResponsesAreRankedByTheProductionRules(string query, string decision, string? id)
    {
        var candidates = GameCandidateAdapter.Project("igdb", Recorded(query));
        var result = SearchCandidateRanker.Rank(GameCandidateAdapter.Context(query), candidates.Select(c => c.Candidate).ToArray());
        Assert.Equal(decision, result.Decision.ToString());
        Assert.Equal(id, result.SelectedIdentity?.Id);
        if (query.Contains("Isaac")) Assert.Contains(result.Candidates.Single(c => c.Identity.Id == "309607").Contradictions, c => c.Code == "pc-platform-mismatch");
    }

    [Fact]
    public void RealPhantomReleaseCanUseTheCleanVariantButDeluxeKeepsItsOwnIdentity()
    {
        var rows = GameCandidateAdapter.Project("igdb", Recorded("Phantom Blade Zero")).Select(c => c.Candidate).ToArray();
        Assert.Equal("250618", SearchCandidateRanker.Rank(GameCandidateAdapter.Context("Phantom.Blade.Zero-voices38"), rows).SelectedIdentity?.Id);
        var context = GameCandidateAdapter.Context("Phantom.Blade.Zero.Deluxe.v302256-17280.WIN.X64.MULTI.PORTABLE.PREACTIVED-NO-TAG");
        Assert.Equal("deluxe", context.GameEdition);
        Assert.Equal("413260", SearchCandidateRanker.Rank(context, rows).SelectedIdentity?.Id);
        Assert.Contains(SearchCandidateRanker.Rank(context, rows).Candidates.Single(c => c.Identity.Id == "250618").Contradictions, c => c.Code == "game-edition-mismatch");
    }

    [Fact]
    public void RemakeNatureDoesNotEraseHomonymsYearsOrRequestedEditions()
    {
        using var doc = JsonDocument.Parse("""
            [{"id":1,"name":"Resident Evil","platforms":[6],"first_release_date":820454400},
             {"id":2,"name":"Resident Evil","platforms":[6],"first_release_date":1009843200,"game_type":{"type":"Remake"}}]
            """);
        var rows = GameCandidateAdapter.Project("igdb", doc.RootElement).Select(c => c.Candidate).ToArray();
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(GameCandidateAdapter.Context("Resident Evil"), rows).Decision);
        Assert.Equal("1", SearchCandidateRanker.Rank(GameCandidateAdapter.Context("Resident Evil (1996)"), rows).SelectedIdentity?.Id);
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(GameCandidateAdapter.Context("Resident Evil Deluxe Edition"), rows).Decision);
        Assert.Equal("2", SearchCandidateRanker.Rank(GameCandidateAdapter.Context("Resident Evil Remake"), rows).SelectedIdentity?.Id);
        Assert.Null(SearchCandidateRanker.Rank(GameCandidateAdapter.Context("Resident Evil 2"), rows).SelectedIdentity);
    }

    [Theory]
    [InlineData("Game DLC Content-voices38")]
    [InlineData("Game Collection-voices38")]
    [InlineData("Game Bundle-voices38")]
    public void ContentVariantCannotAcceptTheBaseGame(string raw)
    {
        using var doc = JsonDocument.Parse("[{\"id\":1,\"name\":\"Game\",\"platforms\":[6]}]");
        var rows = GameCandidateAdapter.Project("igdb", doc.RootElement).Select(c => c.Candidate).ToArray();
        Assert.NotEqual(CandidateDecision.Accept, SearchCandidateRanker.Rank(GameCandidateAdapter.Context(raw), rows).Decision);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(25, 2)]
    [InlineData(26, 4)]
    [InlineData(51, 5)]
    [InlineData(100, 6)]
    public async Task CompletePaginationHonorsSentinelsTerminalProbeAndHeadRecheck(int total, int expectedCalls)
    {
        var data = Items(total); var calls = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit, igdbResponse: (request, _) => {
            if (request.RequestUri!.Host != "api.igdb.com") return Task.FromResult<HttpResponseMessage?>(null);
            calls++; var (offset, limit) = Window(request);
            return Task.FromResult<HttpResponseMessage?>(Json(data.Skip(offset).Take(limit)));
        });
        var complete = await rig.Igdb.ReadMatchingCandidatesAsync("Example", default);
        Assert.Equal(total, complete.GetArrayLength()); Assert.Equal(expectedCalls, calls);
        Assert.Equal(total, GameCandidateAdapter.Project("igdb", complete, completeIgdbCollection: true).Count);
    }

    [Theory]
    [InlineData("over_budget")]
    [InlineData("duplicate")]
    [InlineData("short_page")]
    [InlineData("overlap_drift")]
    [InlineData("head_drift")]
    [InlineData("missing_tail")]
    public async Task IncompleteOrInconsistentCoverageAlwaysAbstains(string mode)
    {
        var data = Items(mode == "over_budget" ? 101 : 51); var calls = 0; var heads = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit, igdbResponse: (request, _) => {
            if (request.RequestUri!.Host != "api.igdb.com") return Task.FromResult<HttpResponseMessage?>(null);
            calls++; var (offset, limit) = Window(request);
            var page = data.Skip(offset).Take(limit).ToArray();
            if (offset == 0 && limit == 26) heads++;
            if (mode == "duplicate" && offset == 0) page[2] = page[1];
            if (mode == "short_page" && offset == 0) page = page.Take(10).ToArray();
            if (mode == "overlap_drift" && offset == 25) page[0] = data[24];
            if (mode == "missing_tail" && offset == 25) page = Array.Empty<JsonElement>();
            if (mode == "head_drift" && heads > 1 && offset == 0) page[0] = JsonSerializer.SerializeToElement(new { id = 1, name = "Changed", platforms = new[] { 6 } });
            return Task.FromResult<HttpResponseMessage?>(Json(page));
        });
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => rig.Igdb.ReadMatchingCandidatesAsync("Example", default));
        Assert.Equal("game_search_coverage_unavailable", error.Message); Assert.InRange(calls, 1, 6);
    }

    [Fact]
    public async Task LastPageCompetitorIsRetainedBeforeAnyAcceptance()
    {
        var data = Items(26); data[0] = JsonSerializer.SerializeToElement(new { id = 1, name = "Game Hit", platforms = new[] { 6 } });
        data[25] = JsonSerializer.SerializeToElement(new { id = 26, name = "Game Hit", platforms = new[] { 6 } });
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit, igdbResponse: (request, _) => {
            if (request.RequestUri!.Host != "api.igdb.com") return Task.FromResult<HttpResponseMessage?>(null);
            var (offset, limit) = Window(request); return Task.FromResult<HttpResponseMessage?>(Json(data.Skip(offset).Take(limit)));
        });
        var all = await rig.Igdb.ReadMatchingCandidatesAsync("Game Hit", default);
        var ranked = SearchCandidateRanker.Rank(GameCandidateAdapter.Context("Game Hit"), GameCandidateAdapter.Project("igdb", all, true).Select(c => c.Candidate).ToArray());
        Assert.Equal(CandidateDecision.Ambiguous, ranked.Decision);
    }

    [Fact]
    public async Task RecordedMotorfestPrefixNeedsASyntheticCompletionForThePaginationUnitTest()
    {
        var prefix = Recorded("The Crew Motorfest");
        Assert.Throws<InvalidDataException>(() => GameCandidateAdapter.Project("igdb", prefix));
        // This is a controlled 26-result endpoint, not a claim that the real search ended at 26.
        var data = prefix.EnumerateArray().ToArray();
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit, igdbResponse: (request, _) => {
            if (request.RequestUri!.Host != "api.igdb.com") return Task.FromResult<HttpResponseMessage?>(null);
            var (offset, limit) = Window(request); return Task.FromResult<HttpResponseMessage?>(Json(data.Skip(offset).Take(limit)));
        });
        var all = await rig.Igdb.ReadMatchingCandidatesAsync("The Crew Motorfest", default);
        Assert.Equal("234256", SearchCandidateRanker.Rank(GameCandidateAdapter.Context("The Crew Motorfest"), GameCandidateAdapter.Project("igdb", all, true).Select(c => c.Candidate).ToArray()).SelectedIdentity?.Id);
    }

    [Fact]
    public async Task CancellationAfterFirstPageCannotPublishAPartialCollection()
    {
        using var cts = new CancellationTokenSource(); var data = Items(26);
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit, igdbResponse: (request, _) => {
            if (request.RequestUri!.Host != "api.igdb.com") return Task.FromResult<HttpResponseMessage?>(null);
            var (offset, limit) = Window(request); if (offset > 0) cts.Cancel();
            return Task.FromResult<HttpResponseMessage?>(Json(data.Skip(offset).Take(limit)));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Igdb.ReadMatchingCandidatesAsync("Game Hit", cts.Token));
    }
}
