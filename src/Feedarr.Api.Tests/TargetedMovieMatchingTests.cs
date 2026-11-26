using System.Net;
using System.Text;
using System.Text.Json;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Posters;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace Feedarr.Api.Tests;

public sealed class TargetedMovieMatchingTests
{
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static object Row(int id = 100, string title = "F1", string date = "2025-06-25") => new {
        id, title, original_title = title, release_date = date, poster_path = "/movie.jpg", original_language = "en" };
    private static HttpResponseMessage Page(params object[] rows) => Json(new {
        page = 1, total_pages = rows.Length == 0 ? 0 : 1, total_results = rows.Length, results = rows });

    [Fact]
    public async Task F1PublishesUsingYearWindowWhileGeneralSearchWouldRequireSixteenPages()
    {
        var calls = new List<(string Title, string Language, int? Year)>();
        var logger = new CaptureLogger();
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true,
            posterLogger: logger, tmdbResponse: (request, _) => {
                if (!request.RequestUri!.AbsolutePath.EndsWith("/search/movie")) return Task.FromResult<HttpResponseMessage?>(null);
                var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                var year = int.TryParse(query["primary_release_year"], out var parsed) ? parsed : (int?)null;
                calls.Add((query["query"]!, query["language"]!, year));
                if (year is null) return Task.FromResult<HttpResponseMessage?>(Json(new {
                    page = 1, total_pages = 8, total_results = 154,
                    results = Enumerable.Range(1, 20).Select(i => Row(i)).ToArray() }));
                return Task.FromResult<HttpResponseMessage?>(year == 2025 ? Page(Row()) : Page());
            });
        var id = rig.IngestRelease("F1.2025.MULTi.1080p.WEB.EAC3.5.1.H265-GL0P", UnifiedCategory.Film);
        var result = await rig.FetchAsync(id);
        Assert.True(result.Ok, JsonSerializer.Serialize(result.Body));
        Assert.Equal(6, calls.Count);
        Assert.All(calls, c => { Assert.Equal("F1", c.Title); Assert.NotNull(c.Year); });
        Assert.Equal(new[] { 2024, 2025, 2026 }, calls.Select(c => c.Year!.Value).Distinct().Order());
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Equal(100, row.TmdbId);
        Assert.Equal("movie", row.TmdbMediaType);
        Assert.NotNull(row.ExtOverview);
        Assert.NotNull(row.PosterFile);
        Assert.Contains(logger.Events, e => Equals(e.GetValueOrDefault("SearchMode"), "targeted_year") && Equals(e.GetValueOrDefault("Outcome"), "complete"));
    }

    [Theory]
    [InlineData(2024)]
    [InlineData(2025)]
    [InlineData(2026)]
    public async Task UnverifiedExactAndAdjacentYearHomonymsNeverPublish(int competitorYear)
    {
        var proofCalls = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true,
            tmdbResponse: (request, _) => {
                if (QueryHelpers.ParseQuery(request.RequestUri!.Query).GetValueOrDefault("append_to_response").ToString().Contains("release_dates")) proofCalls++;
                if (!request.RequestUri.AbsolutePath.EndsWith("/search/movie")) return Task.FromResult<HttpResponseMessage?>(null);
                var year = int.Parse(QueryHelpers.ParseQuery(request.RequestUri.Query)["primary_release_year"]!);
                var rows = new List<object>();
                if (year == 2025) rows.Add(Row());
                if (year == competitorYear) rows.Add(Row(200, date: $"{competitorYear}-01-01"));
                return Task.FromResult<HttpResponseMessage?>(Page(rows.ToArray()));
            });
        var id = rig.IngestRelease("F1.2025.1080p.WEB.x265", UnifiedCategory.Film);
        var result = await rig.FetchAsync(id);
        Assert.False(result.Ok);
        Assert.Contains("ambiguous", JsonSerializer.Serialize(result.Body));
        Assert.Null(rig.GetReleaseForPoster(id)!.TmdbId);
        Assert.Null(rig.GetReleaseForPoster(id)!.PosterFile);
        Assert.Equal(proofCalls, rig.TmdbDetailsCalls);
    }

    [Fact]
    public async Task YearFilterIsPartOfResponseCacheKeyAndCannotBeAppliedToTelevision()
    {
        var calls = new List<int?>();
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, tmdbResponse: (request, _) => {
            var query = QueryHelpers.ParseQuery(request.RequestUri!.Query);
            calls.Add(int.TryParse(query.GetValueOrDefault("primary_release_year"), out var year) ? year : null);
            return Task.FromResult<HttpResponseMessage?>(Page());
        });
        await rig.Tmdb.SearchVideoMatchingPageAsync("F1", "fr-FR", 1, "movie", default, 2025);
        await rig.Tmdb.SearchVideoMatchingPageAsync("F1", "fr-FR", 1, "movie", default, 2025);
        await rig.Tmdb.SearchVideoMatchingPageAsync("F1", "fr-FR", 1, "movie", default, 2024);
        await rig.Tmdb.SearchVideoMatchingPageAsync("F1", "fr-FR", 1, "movie", default);
        await rig.Tmdb.SearchVideoMatchingPageAsync("F1", "en-US", 1, "movie", default, 2025);
        Assert.Equal(new int?[] { 2025, 2024, null, 2025 }, calls);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Tmdb.SearchVideoMatchingPageAsync("F1", "fr-FR", 1, "tv", default, 2025));
        Assert.Equal(4, calls.Count);
    }

    [Fact]
    public void CollectionDiagnosticsExposeOnlyControlledModesAndNumericCounters()
    {
        var logger = new CaptureLogger();
        new VideoCollectionDiagnostic("general_fallback", "not_evaluable_search_budget", 8, 14).Log(logger, 42, UnifiedCategory.Film);
        var fields = Assert.Single(logger.Events);
        Assert.Equal("general_fallback", fields["SearchMode"]);
        Assert.Equal("not_evaluable_search_budget", fields["Outcome"]);
        Assert.Equal(12, fields["PageBudget"]);
        Assert.DoesNotContain(fields.Keys, key => key is "Title" or "Query" or "Url" or "ApiKey" or "Response");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveFallbackPublishesOnlyWhenItsEntirePlanFitsTheRemainingBudget(bool overflow)
    {
        var calls = 0;
        var logger = new CaptureLogger();
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true,
            posterLogger: logger, tmdbResponse: (request, _) => {
                if (!request.RequestUri!.AbsolutePath.EndsWith("/search/movie")) return Task.FromResult<HttpResponseMessage?>(null);
                calls++;
                var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                if (query.ContainsKey("primary_release_year")) return Task.FromResult<HttpResponseMessage?>(Page());
                if (!overflow) return Task.FromResult<HttpResponseMessage?>(Page(Row(title: "Another Film")));
                return Task.FromResult<HttpResponseMessage?>(Json(new {
                    page = 1, total_pages = 8, total_results = 154,
                    results = Enumerable.Range(1, 20).Select(i => Row(i, title: "Another Film")).ToArray() }));
            });
        var id = rig.IngestRelease("Another.Film.2025.1080p.WEB.x265", UnifiedCategory.Film);
        var result = await rig.FetchAsync(id);
        Assert.Equal(!overflow, result.Ok);
        Assert.InRange(calls, 6, 12);
        Assert.Contains(logger.Events, e => Equals(e.GetValueOrDefault("SearchMode"), "general_fallback") &&
            Equals(e.GetValueOrDefault("Outcome"), overflow ? "not_evaluable_search_budget" : "complete"));
        if (overflow)
        {
            Assert.Contains("not_evaluable_search_budget", JsonSerializer.Serialize(result.Body));
            Assert.Null(rig.GetReleaseForPoster(id)!.TmdbId);
            Assert.Null(rig.GetReleaseForPoster(id)!.PosterFile);
            Assert.Equal(0, rig.TmdbDetailsCalls);
        }
        else
        {
            Assert.Equal(8, calls);
            Assert.Equal(100, rig.GetReleaseForPoster(id)!.TmdbId);
            Assert.NotNull(rig.GetReleaseForPoster(id)!.PosterFile);
        }
    }

    private sealed class CaptureLogger : ILogger<PosterFetchService>
    {
        internal List<Dictionary<string, object?>> Events { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> fields) Events.Add(fields.ToDictionary(p => p.Key, p => p.Value));
        }
    }
}
