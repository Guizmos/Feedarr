using System.Net;
using System.Text;
using System.Text.Json;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Tmdb;
using Microsoft.AspNetCore.WebUtilities;

namespace Feedarr.Api.Tests;

// Provider-owned titles/years below are offline fixtures; IDs are synthetic, not live TMDB captures.
public sealed class AnimatedMovieMatchingTests
{
    public static TheoryData<string, string, string, int> Films => new()
    {
        { "Princesse Mononoke", "Princess Mononoke", "もののけ姫", 1997 },
        { "Le Voyage de Chihiro", "Spirited Away", "千と千尋の神隠し", 2001 },
        { "Tokyo Godfathers", "Tokyo Godfathers", "東京ゴッドファーザーズ", 2003 }
    };
    private static string Raw(string title, int year) => $"{title.Replace(' ', '.')}.{year}.MULTi.1080p.BluRay.x265-TEAM";
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static object Row(int id, string title, string original, int year) => new {
        id, title, original_title = original, release_date = $"{year}-01-01", poster_path = "/animated.jpg", original_language = "ja" };
    private static HttpResponseMessage Page(params object[] rows) => Json(new {
        page = 1, total_pages = rows.Length == 0 ? 0 : 1, total_results = rows.Length, results = rows });
    private static CandidateRankingContext Context(string title, int year) => new(
        SearchCandidateGenerator.Generate(new(Raw(title, year), UnifiedCategory.Animation)),
        UnifiedCategory.Animation, SearchMediaType.Movie);
    private static Task<TmdbClient.VideoMatchingPage?> Response(params TmdbClient.SearchResult[] rows) =>
        Task.FromResult<TmdbClient.VideoMatchingPage?>(new(rows, rows.Length == 0 ? 0 : 1, rows.Length));

    [Theory]
    [MemberData(nameof(Films))]
    public async Task AnimatedMovieUsesTheSharedYearWindowAndPublishesWithoutReclassifying(
        string localized, string english, string original, int year)
    {
        var requests = new List<(int Year, string Language)>();
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true,
            tmdbResponse: (request, _) => {
                Assert.DoesNotContain("/search/tv", request.RequestUri!.AbsolutePath);
                if (!request.RequestUri.AbsolutePath.EndsWith("/search/movie")) return Task.FromResult<HttpResponseMessage?>(null);
                var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                var requestedYear = int.Parse(query["primary_release_year"]!);
                var language = query["language"].ToString();
                requests.Add((requestedYear, language));
                Assert.Equal(localized, query["query"].ToString());
                return Task.FromResult<HttpResponseMessage?>(requestedYear == year
                    ? Page(Row(100, language == "fr-FR" ? localized : english, original, year)) : Page());
            });
        var id = rig.IngestRelease(Raw(localized, year), UnifiedCategory.Animation);
        Assert.Equal("movie", PosterFetchService.ResolveVideoType(rig.GetReleaseForPoster(id)!, UnifiedCategory.Animation));
        var result = await rig.FetchAsync(id);
        Assert.True(result.Ok, JsonSerializer.Serialize(result.Body));
        Assert.Equal(6, requests.Count);
        Assert.Equal(new[] { year - 1, year, year + 1 }, requests.Select(r => r.Year).Distinct().Order());
        Assert.All(new[] { "fr-FR", "en-US" }, locale => Assert.Equal(3, requests.Count(r => r.Language == locale)));
        var saved = rig.GetReleaseForPoster(id)!;
        Assert.Equal("Animation", saved.UnifiedCategory);
        Assert.Equal(localized, saved.TitleClean);
        Assert.Equal(year, saved.Year);
        Assert.Equal(100, saved.TmdbId);
        Assert.Equal("movie", saved.TmdbMediaType);
        Assert.NotNull(saved.PosterFile);
        Assert.False(string.IsNullOrWhiteSpace(saved.ExtOverview));
        Assert.True((await rig.FetchAsync(id)).Ok);
        Assert.Equal(6, requests.Count);
    }

    [Theory]
    [MemberData(nameof(Films))]
    public async Task OriginalAndLocalizedTitlesAreKeptAcrossTheTwoLanguages(
        string localized, string english, string original, int year)
    {
        foreach (var title in new[] { localized, original })
        {
            var context = Context(title, year);
            Assert.Equal(year, context.Queries.Single().Year);
            var collected = await MovieYearSearchCollector.CollectAsync(context, new[] { "fr-FR", "en-US" },
                (_, language, _, selectedYear, _) => selectedYear == year
                    ? Response(new TmdbClient.SearchResult(100, language == "fr-FR" ? localized : english, original, "/animated.jpg", "movie", year, "ja"))
                    : Response(), default);
            Assert.True(collected.Complete);
            Assert.Equal("targeted_year", collected.SearchMode);
            var projected = TmdbShadowCandidateAdapter.Project(collected.Observed, context);
            Assert.Single(projected);
            var ranked = SearchCandidateRanker.Rank(context, projected);
            Assert.Equal(CandidateDecision.Accept, ranked.Decision);
            Assert.Equal(1, ranked.Candidates.Single().Confidence);
            Assert.Contains(title == original ? "official-original-title-exact" : "official-localized-title-exact", ranked.Candidates[0].PositiveReasons);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task AnimatedMovieHomonymsAndNeighboringYearsStillAbstain(int offset)
    {
        var context = Context("Princesse Mononoke", 1997);
        var collection = await MovieYearSearchCollector.CollectAsync(context, new[] { "fr-FR", "en-US" }, (_, _, _, year, _) => {
            var rows = new List<TmdbClient.SearchResult>();
            if (year == 1997) rows.Add(new(100, "Princesse Mononoke", "もののけ姫", null, "movie", 1997, "ja"));
            if (year == 1997 + offset) rows.Add(new(200, "Princesse Mononoke", "Other original", null, "movie", 1997 + offset, "ja"));
            return Response(rows.ToArray());
        }, default);
        Assert.True(collection.Complete);
        var ranked = SearchCandidateRanker.Rank(context, TmdbShadowCandidateAdapter.Project(collection.Observed, context));
        Assert.Equal(CandidateDecision.Ambiguous, ranked.Decision);
        Assert.Contains("close-competing-identities", ranked.Reasons);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnimatedMovieUsesVerifiedOfficialAliasesWithoutIgnoringOwnership(bool validOwner)
    {
        var aliasCalls = 0;
        var searches = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true,
            tmdbResponse: (request, _) => {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/search/movie"))
                {
                    searches++;
                    var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                    var selectedYear = query.GetValueOrDefault("primary_release_year").ToString();
                    return Task.FromResult<HttpResponseMessage?>(selectedYear is "" or "1997"
                        ? Page(Row(100, "Princess Mononoke", "もののけ姫", 1997)) : Page());
                }
                if (path.EndsWith("/movie/100/alternative_titles"))
                {
                    aliasCalls++;
                    return Task.FromResult<HttpResponseMessage?>(Json(new {
                        id = validOwner ? 100 : 999, titles = new[] { new { title = "Mononoke Hime" } } }));
                }
                return Task.FromResult<HttpResponseMessage?>(null);
            });
        var id = rig.IngestRelease(Raw("Mononoke Hime", 1997), UnifiedCategory.Animation);
        var result = await rig.FetchAsync(id);
        Assert.Equal(validOwner, result.Ok);
        Assert.Equal(1, aliasCalls);
        Assert.Equal(8, searches); // Complete targeted plan, then complete general fallback before enrichment.
        Assert.Equal(validOwner ? 100 : (int?)null, rig.GetReleaseForPoster(id)!.TmdbId);
        Assert.Equal(validOwner, rig.GetReleaseForPoster(id)!.PosterFile is not null);
    }

    [Fact]
    public async Task LastPageAnimatedCompetitorIsNeverHidden()
    {
        var context = Context("Le Voyage de Chihiro", 2001);
        var calls = 0;
        var collected = await MovieYearSearchCollector.CollectAsync(context, new[] { "fr-FR", "en-US" }, (_, _, page, year, _) => {
            calls++;
            if (year != 2001) return Response();
            var rows = page == 1 ? Enumerable.Range(1, 20).Select(i => new TmdbClient.SearchResult(i,
                i == 1 ? "Le Voyage de Chihiro" : $"Other Film {i}", null, null, "movie", 2001, "ja")).ToArray()
                : new[] { new TmdbClient.SearchResult(200, "Le Voyage de Chihiro", null, null, "movie", 2001, "ja") };
            return Task.FromResult<TmdbClient.VideoMatchingPage?>(new(rows, 2, 21));
        }, default);
        Assert.True(collected.Complete);
        Assert.Equal(8, calls);
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(context,
            TmdbShadowCandidateAdapter.Project(collected.Observed, context)).Decision);
    }

    [Theory]
    [InlineData(1998, true)]
    [InlineData(2024, false)]
    public async Task ReleaseYearsKeepTheExistingToleranceAndNeverBecomeTheOriginalYear(int releaseYear, bool accepted)
    {
        var context = Context("Princesse Mononoke", releaseYear);
        var collected = await MovieYearSearchCollector.CollectAsync(context, new[] { "fr-FR", "en-US" }, (_, _, _, selectedYear, _) =>
            selectedYear is null or 1997
                ? Response(new TmdbClient.SearchResult(100, "Princesse Mononoke", "もののけ姫", null, "movie", 1997, "ja")) : Response(), default);
        Assert.True(collected.Complete);
        Assert.Equal(accepted ? "targeted_year" : "general_fallback", collected.SearchMode);
        var ranked = SearchCandidateRanker.Rank(context, TmdbShadowCandidateAdapter.Project(collected.Observed, context));
        Assert.Equal(accepted ? CandidateDecision.Accept : CandidateDecision.Reject, ranked.Decision);
        Assert.Equal(releaseYear, context.Queries.Single().Year);
        if (!accepted) Assert.Contains(ranked.Candidates.Single().Contradictions, c => c.Code == "parsed-year-mismatch");
    }

    [Theory]
    [InlineData("Princesse.Mononoke.1997.REMASTERED.1080p.BluRay.x265-TEAM", "Princesse Mononoke", 1997, true)]
    [InlineData("Princesse.Mononoke.1997.REMASTERED.2024.MULTi.1080p.BluRay.x265-TEAM", "Princesse Mononoke 1997 REMASTERED", 2024, false)]
    [InlineData("Tokyo.Godfathers.2003.REMASTERED.2024.MULTi.1080p.BluRay.x265-TEAM", "Tokyo Godfathers 2003 REMASTERED", 2024, false)]
    public async Task RemastersDoNotRewriteTheParsedTitleOrPromoteDistributionDates(
        string raw, string expectedQuery, int expectedYear, bool accepted)
    {
        var workYear = raw.StartsWith("Tokyo", StringComparison.Ordinal) ? 2003 : 1997;
        var official = workYear == 2003 ? "Tokyo Godfathers" : "Princesse Mononoke";
        var context = new CandidateRankingContext(SearchCandidateGenerator.Generate(new(raw, UnifiedCategory.Animation)),
            UnifiedCategory.Animation, SearchMediaType.Movie);
        Assert.Equal(expectedQuery, context.Queries.Single().Title);
        Assert.Equal(expectedYear, context.Queries.Single().Year);
        var collected = await MovieYearSearchCollector.CollectAsync(context, new[] { "fr-FR", "en-US" }, (_, _, _, year, _) =>
            year is null || year == workYear
                ? Response(new TmdbClient.SearchResult(100, official, official, null, "movie", workYear, "ja")) : Response(), default);
        Assert.True(collected.Complete);
        var ranked = SearchCandidateRanker.Rank(context, TmdbShadowCandidateAdapter.Project(collected.Observed, context));
        Assert.Equal(accepted ? CandidateDecision.Accept : CandidateDecision.Reject, ranked.Decision);
        Assert.Equal(expectedQuery, context.Queries.Single().Title);
        Assert.Equal(expectedYear, context.Queries.Single().Year);
        if (!accepted) Assert.Contains(ranked.Candidates.Single().Contradictions, c => c.Code == "parsed-year-mismatch");
    }

    [Theory]
    [InlineData(UnifiedCategory.Animation)]
    [InlineData(UnifiedCategory.Serie)]
    public async Task AnimatedSeriesStayOnTheGeneralTelevisionPlan(UnifiedCategory category)
    {
        var snapshot = new ReleaseForPoster { Title = "Animation.Show.S01E02.2001.1080p.WEB", MediaType = "series", CategoryIds = "2020" };
        Assert.Equal("tv", PosterFetchService.ResolveVideoType(snapshot, category));
        var context = new CandidateRankingContext(
            SearchCandidateGenerator.Generate(new(snapshot.Title, UnifiedCategory.Serie)), category, SearchMediaType.Series, Season: 1, Episode: 2);
        var requests = 0;
        var collected = await MovieYearSearchCollector.CollectAsync(context, new[] { "fr-FR", "en-US" }, (_, _, _, year, _) => {
            requests++; Assert.Null(year); return Response();
        }, default);
        Assert.True(collected.Complete);
        Assert.Equal("general", collected.SearchMode);
        Assert.Equal(2, requests);
    }
}
