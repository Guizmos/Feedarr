using System.Text.Json;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Tests;

public sealed class MovieTitleEvidenceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProductionFetchUsesVerifiedAliasesBeforePublication(bool valid)
    {
        var aliasCalls = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit,
            completeTmdbMetadata: true, tmdbResponse: (request, _) => {
                if (!request.RequestUri!.AbsolutePath.EndsWith("/movie/100/alternative_titles"))
                    return Task.FromResult<System.Net.Http.HttpResponseMessage?>(null);
                aliasCalls++;
                var payload = new { id = valid ? 100 : 999, titles = new[] { new { title = "A Local Film" } } };
                return Task.FromResult<System.Net.Http.HttpResponseMessage?>(new(System.Net.HttpStatusCode.OK) {
                    Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
                });
            });
        var id = rig.IngestRelease("A.Local.Film.1999.FRENCH.1080p.WEB.H264-TEAM", UnifiedCategory.Film);
        var result = await rig.FetchAsync(id);
        Assert.Equal(1, aliasCalls);
        Assert.Equal(valid, result.Ok);
        var release = rig.GetReleaseForPoster(id)!;
        if (valid) { Assert.Equal(100, release.TmdbId); Assert.NotNull(release.PosterFile); }
        else { Assert.Null(release.TmdbId); Assert.Null(release.PosterFile); }
    }

    private static CandidateRankingContext Context(string title = "A Local Film", int? year = 2020,
        CandidateYearEvidence evidence = CandidateYearEvidence.Parsed) => new(
        new[] { new SearchCandidate(title, year, evidence, SearchCandidateOrigin.Primary) },
        UnifiedCategory.Film, SearchMediaType.Movie);

    private static ProviderSearchCandidate Movie(string id = "1", string title = "Original foreign title", int? year = 2020)
        => new(new("tmdb", SearchMediaType.Movie, id), title, year);

    private static JsonElement? Aliases(string id, params string[] titles)
        => JsonSerializer.SerializeToElement(new { id = int.Parse(id), titles = titles.Select(title => new { title }) });

    [Theory]
    [InlineData("Fast.And.Furious.6.2013.MULTI.VFF.1080p.BluRay.x265-GWEN", "Fast & Furious 6", "Fast and Furious 6", 2013)]
    [InlineData("Le.Noel.du.coeur.2017.FRENCH.1080p.WEB.H264-TEAM", "A foreign title", "Le Noël du coeur", 2017)]
    public async Task OfficialAliasCanRepairTitleWithoutLoweringThresholds(string raw, string responseTitle, string alias, int year)
    {
        // Provider responses are fixtures, not recovered historical TMDB responses.
        var context = new CandidateRankingContext(SearchCandidateGenerator.Generate(new(raw, UnifiedCategory.Film)),
            UnifiedCategory.Film, SearchMediaType.Movie);
        var rows = new[] { Movie(title: responseTitle, year: year) };
        Assert.NotEqual(CandidateDecision.Accept, SearchCandidateRanker.Rank(context, rows).Decision);
        var enriched = await MovieTitleCandidates.EnrichAsync(context, rows,
            (id, _) => Task.FromResult(Aliases(id, alias)), default);
        var ranked = SearchCandidateRanker.Rank(context, enriched);
        Assert.Equal(CandidateDecision.Accept, ranked.Decision);
        Assert.Contains("official-alternative-title-exact", ranked.Candidates.Single().PositiveReasons);
        Assert.Equal(rows[0].Identity, ranked.SelectedIdentity);
    }

    [Theory]
    [InlineData(2019)]
    [InlineData(2020)]
    [InlineData(2021)]
    public async Task EveryRepairableCompetitorIsEnrichedAndAmbiguityRemains(int competitorYear)
    {
        var calls = new List<string>();
        var rows = new[] { Movie(), Movie("2", "Another title", competitorYear), Movie("3", year: 1980) };
        var enriched = await MovieTitleCandidates.EnrichAsync(Context(), rows, (id, _) => {
            calls.Add(id); return Task.FromResult(Aliases(id, "A Local Film"));
        }, default);
        Assert.Equal(new[] { "1", "2" }, calls);
        Assert.Equal(rows.Select(r => r.Identity), enriched.Select(r => r.Identity));
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(Context(), enriched).Decision);
        Assert.Equal(rows[2], enriched[2]);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(2020, true)]
    public async Task AliasAloneDoesNotSupplyIndependentYearEvidence(int? year, bool hypothesis)
    {
        var context = Context(year: year, evidence: hypothesis ? CandidateYearEvidence.Hypothesis : CandidateYearEvidence.None);
        var enriched = await MovieTitleCandidates.EnrichAsync(context, new[] { Movie() },
            (id, _) => Task.FromResult(Aliases(id, "A Local Film")), default);
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(context, enriched).Decision);
    }

    [Fact]
    public async Task MissingAndConflictingMetadataArePreserved()
    {
        var rows = new[] { Movie(year: null), Movie("2"), Movie("2", year: 1980) };
        var enriched = await MovieTitleCandidates.EnrichAsync(Context(), rows,
            (id, _) => Task.FromResult(Aliases(id, "A Local Film")), default);
        Assert.Equal(rows.Select(r => (r.Identity, r.Year)), enriched.Select(r => (r.Identity, r.Year)));
        Assert.NotEqual(CandidateDecision.Accept, SearchCandidateRanker.Rank(Context(), enriched).Decision);
    }

    [Theory]
    [InlineData(6, 6)]
    [InlineData(7, 0)]
    public async Task EvidenceBudgetNeverEnrichesAPartialCompetitorSet(int count, int expectedCalls)
    {
        var rows = Enumerable.Range(1, count).Select(i => Movie(i.ToString())).ToArray();
        var calls = 0;
        var enriched = await MovieTitleCandidates.EnrichAsync(Context(), rows, (id, _) => {
            calls++; return Task.FromResult(Aliases(id));
        }, default);
        Assert.Equal(expectedCalls, calls);
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(Context(), enriched).Decision);
        if (expectedCalls == 0) Assert.Same(rows, enriched);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"id\":2,\"titles\":[]}")]
    [InlineData("{\"id\":1}")]
    [InlineData("{\"id\":1,\"titles\":[{\"title\":\"\"}]}")]
    public async Task MissingOrForeignEvidenceCannotReachRanking(string json)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => MovieTitleCandidates.EnrichAsync(Context(), new[] { Movie() },
            (_, _) => Task.FromResult<JsonElement?>(JsonSerializer.Deserialize<JsonElement>(json)), default));
    }

    [Fact]
    public async Task FailureAfterFirstResponseCannotReturnPartiallyEnrichedRows()
    {
        var rows = new[] { Movie(), Movie("2") };
        await Assert.ThrowsAsync<HttpRequestException>(() => MovieTitleCandidates.EnrichAsync(Context(), rows,
            (id, _) => id == "1" ? Task.FromResult(Aliases(id, "A Local Film")) : throw new HttpRequestException(), default));
        Assert.All(rows, r => Assert.Null(r.OfficialTitles));
    }

    [Fact]
    public async Task ExistingAcceptanceAndNonMovieTypesDoNotFetchAliases()
    {
        Task<JsonElement?> Unexpected(string id, CancellationToken ct) => throw new Exception("Unexpected request");
        var rows = new[] { Movie(title: "A Local Film") };
        Assert.Same(rows, await MovieTitleCandidates.EnrichAsync(Context(), rows, Unexpected, default));
        foreach (var mediaType in Enum.GetValues<SearchMediaType>().Where(t => t != SearchMediaType.Movie))
            Assert.Same(rows, await MovieTitleCandidates.EnrichAsync(Context() with { ExpectedMediaType = mediaType }, rows, Unexpected, default));
    }

    [Fact]
    public async Task CancellationAndNumericTitleProtectionRemainEffective()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MovieTitleCandidates.EnrichAsync(Context(), new[] { Movie() },
            (_, _) => throw new Exception("Unexpected request"), cancelled.Token));
        var context = Context("1917", 2019);
        var enriched = await MovieTitleCandidates.EnrichAsync(context, new[] { Movie(year: 2019) },
            (id, _) => Task.FromResult(Aliases(id, "1917")), default);
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(context, enriched).Decision);
    }
}
