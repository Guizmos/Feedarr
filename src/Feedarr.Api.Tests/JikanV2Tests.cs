using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Tests;

public sealed class JikanV2Tests
{
    private static JsonElement Item(int id = 1, string title = "Naruto", string type = "TV", int year = 2002, params string[] aliases) => JsonSerializer.SerializeToElement(new
    { mal_id = id, title, title_synonyms = aliases, title_english = title, title_japanese = "ナルト", year, type, episodes = 220, relations = Array.Empty<object>() });
    private static JsonElement Page(params JsonElement[] rows) => JsonSerializer.SerializeToElement(new
    { data = rows, pagination = new { current_page = 1, has_next_page = false, items = new { total = rows.Length } } });

    [Theory]
    [InlineData("TV")]
    [InlineData("Movie")]
    [InlineData("OVA")]
    [InlineData("ONA")]
    [InlineData("Special")]
    [InlineData("TV Special")]
    public void FormatsAndOfficialJapaneseTitlesUseTheCommonRanker(string format)
    {
        var candidate = JikanCandidateAdapter.Project(Item(type: format));
        var context = JikanCandidateAdapter.Context("ナルト.2002.1080p.BluRay.x264-GRP") with { AnimeFormat = format };
        Assert.Equal(CandidateDecision.Accept, SearchCandidateRanker.Rank(context, new[] { candidate }).Decision);
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(context with { AnimeFormat = format == "TV" ? "Movie" : "TV" }, new[] { candidate }).Decision);
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(context with { Queries = new[] { new SearchCandidate("ナルト", null, CandidateYearEvidence.None, SearchCandidateOrigin.Primary) } }, new[] { candidate }).Decision);
    }

    [Fact]
    public void UnsupportedFormatsAreContradictionsAndMissingFormatIsNotPositive()
    {
        var context = JikanCandidateAdapter.Context("Naruto.2002.1080p.BluRay.x264-GRP");
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(context, new[] { JikanCandidateAdapter.Project(Item(type: "Music")) }).Decision);
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(context, new[] { JikanCandidateAdapter.Project(Item(type: "")) }).Decision);
    }

    [Fact]
    public void ExplicitFormatAnnotationsAreQueryContextNotTitleWords()
    {
        var movie = JikanCandidateAdapter.Context("[Movie] Naruto.2002.1080p.BluRay.x264-GRP");
        Assert.Equal("Movie", movie.AnimeFormat); Assert.Equal("Naruto", movie.Queries.Single().Title);
        var ova = JikanCandidateAdapter.Context("Naruto.OVA.2002.1080p.BluRay.x264-GRP");
        Assert.Equal("OVA", ova.AnimeFormat); Assert.Equal("Naruto", ova.Queries.Single().Title);
        var title = JikanCandidateAdapter.Context("Special.A.2008.1080p.WEB.x264-GRP");
        Assert.Null(title.AnimeFormat); Assert.Contains("Special", title.Queries.Single().Title);
    }

    [Fact]
    public async Task SecretTitleWordIsAllowedButCredentialsNeverLeaveTheAdapter()
    {
        var calls = 0;
        Task<JsonElement?> Read(string _, CancellationToken token) { calls++; return Task.FromResult<JsonElement?>(Page()); }
        Assert.Empty(await JikanCandidateAdapter.CollectAsync("The.Secret.World.2010.1080p.BluRay", Read, default));
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<InvalidDataException>(() => JikanCandidateAdapter.CollectAsync("Anime.passkey=private-value", Read, default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void StructuredProviderTitlesDoNotRequireDeprecatedFields()
    {
        var item = JsonSerializer.SerializeToElement(new { mal_id = 1, type = "TV", year = 2002,
            titles = new[] { new { type = "Default", title = "Naruto" }, new { type = "English", title = "Naruto English" }, new { type = "Japanese", title = "ナルト" } } });
        var candidate = JikanCandidateAdapter.Project(item);
        Assert.Equal("Naruto", candidate.Title);
        Assert.Equal("Naruto English", JikanCandidateAdapter.ProviderTitle(item, "English", "title_english"));
        Assert.Equal(CandidateDecision.Accept, SearchCandidateRanker.Rank(JikanCandidateAdapter.Context("ナルト.2002.1080p.BluRay.x264-GRP"), new[] { candidate }).Decision);
    }

    [Fact]
    public async Task RemakesWithTheSameTitleAndSeasonRemainAmbiguous()
    {
        var rows = new[] { Item(title: "Naruto 1st Season"), Item(2, title: "Naruto 1st Season", year: 2020) };
        var calls = 0;
        var observed = await JikanCandidateAdapter.CollectAsync("Naruto.S01.1080p.WEB.x264-GRP", (path, _) => {
            calls++;
            return Task.FromResult<JsonElement?>(path.Contains('?') ? Page(rows) : JsonSerializer.SerializeToElement(new { data = rows[path.Contains("/1/") ? 0 : 1] }));
        }, default);
        Assert.Equal(3, calls);
        var ranked = SearchCandidateRanker.Rank(JikanCandidateAdapter.Context("Naruto.S01.1080p.WEB.x264-GRP"), observed.Select(o => o.Candidate).ToArray());
        Assert.Equal(CandidateDecision.Ambiguous, ranked.Decision);
        Assert.Contains("close-competing-identities", ranked.Reasons);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task DeclaredSeasonOrdinalCannotPublishAsAnotherSeason(int season, bool accept)
    {
        var full = Item(title: "Naruto 2nd Season", aliases: ["Naruto"]);
        var raw = $"Naruto.S0{season}.1080p.WEB.x264-GRP";
        var rows = await JikanCandidateAdapter.CollectAsync(raw, (path, _) => Task.FromResult<JsonElement?>(path.Contains('?') ? Page(full) : JsonSerializer.SerializeToElement(new { data = full })), default);
        var ranked = SearchCandidateRanker.Rank(JikanCandidateAdapter.Context(raw), rows.Select(r => r.Candidate).ToArray());
        Assert.Equal(accept ? CandidateDecision.Accept : CandidateDecision.Reject, ranked.Decision);
        if (!accept) Assert.Contains(ranked.Candidates.SelectMany(c => c.Contradictions), c => c.Code == "season-mismatch" && c.IsStrong);
    }

    [Fact]
    public async Task UnknownCoverageAndWrongMalIdentityNeverBecomeMatches()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => JikanCandidateAdapter.CollectAsync("Naruto.S01", (_, _) => Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { data = new[] { Item() } })), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => JikanCandidateAdapter.CollectAsync("Naruto.S01", (path, _) => Task.FromResult<JsonElement?>(path.Contains('?') ? Page(Item()) : JsonSerializer.SerializeToElement(new { data = Item(2) })), default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JikanCandidateAdapter.CollectAsync("Naruto", async (_, token) => { await Task.Delay(1000, token); return null; }, cancellation.Token));
    }

    [Fact]
    public async Task AbsoluteEpisodeNeedsProviderProofAndAlternativesStayUnresolved()
    {
        var raw = "Naruto.E07.1080p.WEB.x264-GRP";
        async Task<IReadOnlyList<JikanObservation>> Collect(bool episodeKnown) => await JikanCandidateAdapter.CollectAsync(raw, (path, _) => {
            JsonElement response = path.Contains('?') ? Page(Item()) : path.Contains("/episodes/")
                ? JsonSerializer.SerializeToElement(new { data = new { mal_id = 7, aired = episodeKnown ? "2002-11-14T00:00:00+00:00" : null } })
                : JsonSerializer.SerializeToElement(new { data = Item() });
            return Task.FromResult<JsonElement?>(response);
        }, default);
        Assert.Equal(CandidateDecision.Accept, SearchCandidateRanker.Rank(JikanCandidateAdapter.Context(raw), (await Collect(true)).Select(r => r.Candidate).ToArray()).Decision);
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(JikanCandidateAdapter.Context(raw), (await Collect(false)).Select(r => r.Candidate).ToArray()).Decision);
        var candidate = JikanCandidateAdapter.Project(Item());
        candidate = candidate with { Anime = candidate.Anime! with { AlternativeIdentityUnverified = true } };
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(JikanCandidateAdapter.Context("Naruto.2002.1080p.BluRay.x264-GRP"), new[] { candidate }).Decision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationIsAtomicAndPreservesExistingAndManualIdentities(bool fail)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.AnimeJikanHit);
        var id = rig.IngestRelease("Naruto.2002.1080p.BluRay.x264-GRP", UnifiedCategory.Anime);
        if (fail) { using var db = rig.Database.Open(); db.Execute("CREATE TRIGGER stop_anime BEFORE UPDATE OF ext_provider ON media_entities BEGIN SELECT RAISE(ABORT, 'test'); END"); }
        var result = await rig.FetchAsync(id);
        var row = rig.GetReleaseForPoster(id)!;
        if (fail)
        { Assert.False(result.Ok); Assert.Null(row.ExtProviderId); Assert.Null(row.PosterFile); return; }
        Assert.True(result.Ok, JsonSerializer.Serialize(result.Body)); Assert.Equal("jikan", row.ExtProvider); Assert.Equal("1500", row.ExtProviderId);
        Assert.Equal("Mock anime synopsis", row.ExtOverview); Assert.Contains("-v2-", row.PosterFile!);
        Assert.True((await rig.FetchAsync(id)).Ok); Assert.Equal(row.PosterFile, rig.GetReleaseForPoster(id)!.PosterFile);
        rig.Repository.SetManualMatch(id, "jikan", "anime", "1501");
        Assert.True((await rig.FetchAsync(id)).Ok); Assert.Equal("1501", rig.GetReleaseForPoster(id)!.ManualMatchId);
    }

    [Fact]
    public async Task EmptyRelationsDoNotProveSeasonOneAndPagesStayBounded()
    {
        var raw = "Naruto.S01.1080p.WEB.x264-GRP";
        var rows = await JikanCandidateAdapter.CollectAsync(raw, (path, _) => Task.FromResult<JsonElement?>(path.Contains('?') ? Page(Item()) : JsonSerializer.SerializeToElement(new { data = Item() })), default);
        Assert.Null(rows[0].Candidate.Season);
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(JikanCandidateAdapter.Context(raw), rows.Select(r => r.Candidate).ToArray()).Decision);
        var calls = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => JikanCandidateAdapter.CollectAsync(raw, (_, _) => {
            calls++; return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { data = new[] { Item(calls) },
                pagination = new { current_page = calls, has_next_page = true, items = new { total = 100 } } }));
        }, default));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(1, false, false, false)]
    [InlineData(2, false, true, false)]
    [InlineData(2, false, true, true)]
    [InlineData(2, true, false, false)]
    public async Task SharedEntitiesNeverPropagateMalAcrossSeasonsOrManualTitles(int otherSeason, bool manual, bool accepted, bool legacyCase)
    {
        var item = JsonSerializer.SerializeToElement(new { mal_id = 1500, title = "Naruto 2nd Season", title_synonyms = new[] { "Naruto" },
            type = "TV", year = 2007, episodes = 12, relations = Array.Empty<object>(),
            images = new { jpg = new { large_image_url = "https://cdn.jikan.moe/images/anime/naruto.jpg" } } });
        var images = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.AnimeJikanHit, jikanResponse: (request, _) => {
            if (request.RequestUri!.Host == "cdn.jikan.moe") { images++; return Task.FromResult<HttpResponseMessage?>(null); }
            var response = request.RequestUri.AbsolutePath.EndsWith("/full") ? JsonSerializer.Serialize(new { data = item }) : JsonSerializer.Serialize(Page(item));
            return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") });
        });
        var target = rig.IngestRelease("Naruto.S02.1080p.WEB.x264-GRP", UnifiedCategory.Anime);
        var sibling = rig.IngestRelease($"{(legacyCase ? "NARUTO" : "Naruto")}.S0{otherSeason}.720p.BluRay.x265-OTHER", UnifiedCategory.Anime);
        if (legacyCase)
        {
            // Simulate a historical entity merge; ordinary ingestion preserves title case.
            using var db = rig.Database.Open();
            db.Execute("UPDATE releases SET entity_id=(SELECT entity_id FROM releases WHERE id=@target) WHERE id=@sibling", new { target, sibling });
        }
        Assert.Equal(rig.GetReleaseForPoster(target)!.EntityId, rig.GetReleaseForPoster(sibling)!.EntityId);
        if (manual) rig.SetManualTitleForTest(sibling);
        var result = await rig.FetchAsync(target);
        Assert.Equal(1, images); // Reached real image preparation, not an earlier ranking abstention.
        Assert.Equal(accepted, result.Ok);
        if (!accepted)
        {
            Assert.Equal(422, result.StatusCode);
            Assert.Contains("jikan_shared_entity_scope_unverified", JsonSerializer.Serialize(result.Body));
            Assert.Null(rig.GetReleaseForPoster(target)!.ExtProviderId); Assert.Null(rig.GetReleaseForPoster(sibling)!.ExtProviderId);
            Assert.Null(rig.GetReleaseForPoster(target)!.PosterFile); Assert.Null(rig.GetReleaseForPoster(sibling)!.PosterFile);
        }
        else
        {
            Assert.Equal("1500", rig.GetReleaseForPoster(sibling)!.ExtProviderId);
            Assert.True((await rig.FetchAsync(sibling)).Ok); Assert.Equal(1, images);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndStaleResultsCannotPublish(bool cancel)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PosterMatchingContractTestRig? current = null; long id = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.AnimeJikanHit, jikanResponse: async (request, token) => {
            if (cancel && request.RequestUri!.AbsolutePath.EndsWith("/full")) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            if (!cancel && request.RequestUri!.Host == "cdn.jikan.moe") current!.SetManualTitleForTest(id);
            return null;
        });
        current = rig; id = rig.IngestRelease("Naruto.2002.1080p.BluRay.x264-GRP", UnifiedCategory.Anime);
        using var cancellation = new CancellationTokenSource(); var task = rig.FetchAsync(id, cancellation.Token);
        if (cancel) {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        } else { Assert.Equal(409, (await task).StatusCode); }
        Assert.Null(rig.GetReleaseForPoster(id)!.ExtProviderId); Assert.Null(rig.GetReleaseForPoster(id)!.PosterFile);
    }
}
