using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Tests;

public sealed class DirectRssVideoRoutingTests
{
    [Theory]
    [InlineData(UnifiedCategory.Animation, 900)]
    [InlineData(UnifiedCategory.Spectacle, 909)]
    public async Task PersistedDirectRssMovieCanBeRetriedWithoutReimport(UnifiedCategory category, int categoryId)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true);
        var id = rig.IngestRelease("The.Matrix.1999.1080p.BluRay.x264-GRP", category);
        using (var conn = rig.Database.Open())
        {
            conn.Execute("UPDATE sources SET source_kind='rss' WHERE id=1");
            conn.Execute("UPDATE releases SET category_id=@categoryId,category_ids=@ids,std_category_id=NULL,spec_category_id=NULL,title_clean='obsolete',year=NULL,poster_last_error='v2:collection:coverage_insufficient' WHERE id=@id", new { id, categoryId, ids = categoryId.ToString() });
        }
        var persisted = rig.GetReleaseForPoster(id)!;
        Assert.True(persisted.IsDirectRss);
        Assert.Equal("movie", PosterFetchService.ResolveVideoType(persisted, category));
        Assert.True((await rig.FetchAsync(id)).Ok);
        var saved = rig.GetReleaseForPoster(id)!;
        Assert.Equal(100, saved.TmdbId);
        Assert.Equal(categoryId, saved.CategoryId);
        Assert.Null(saved.StdCategoryId);
        Assert.Equal(category.ToString(), saved.UnifiedCategory);
        Assert.All(rig.TmdbSearchRequests, uri => Assert.Contains("primary_release_year=", uri.Query));
        Assert.Equal(6, rig.TmdbSearchRequests.Count);
    }

    [Theory]
    [InlineData(UnifiedCategory.Animation, "Movie.2025.1080p.WEB", "movie")]
    [InlineData(UnifiedCategory.Spectacle, "Movie.2025.1080p.WEB", "movie")]
    [InlineData(UnifiedCategory.Animation, "Show.S01E02.2025.1080p.WEB", "tv")]
    [InlineData(UnifiedCategory.Spectacle, "Show.S01E02.2025.1080p.WEB", "tv")]
    [InlineData(UnifiedCategory.Animation, "Show.S01.2025.1080p.WEB", "tv")]
    [InlineData(UnifiedCategory.Spectacle, "Show.2025.11.14.1080p.WEB", "tv")]
    [InlineData(UnifiedCategory.Animation, "Movie.1080p.WEB", null)]
    public void DirectRssRequiresParsedMovieEvidenceAndPreservesTv(UnifiedCategory category, string raw, string? expected)
    {
        var parsed = new TitleParser().Parse(raw, category);
        var row = new ReleaseForPoster { IsDirectRss = true, Title = raw, TitleClean = parsed.TitleClean,
            Year = parsed.Year, MediaType = "movie", CategoryId = category == UnifiedCategory.Animation ? 900 : 909 };
        Assert.Equal(expected, PosterFetchService.ResolveVideoType(row, category));
        row.IsDirectRss = false;
        if (expected == "movie") Assert.Null(PosterFetchService.ResolveVideoType(row, category));
        row.CategoryIds = "2040";
        if (expected == "movie") Assert.Equal("movie", PosterFetchService.ResolveVideoType(row, category));
        row.IsDirectRss = true;
        row.CategoryIds = "2040,5040";
        Assert.Null(PosterFetchService.ResolveVideoType(row, category));
    }

    [Fact]
    public void DefaultMovieAndInconsistentPersistedEvidenceDoNotProveMovie()
    {
        var row = new ReleaseForPoster { IsDirectRss = true, Title = "Movie.2025.1080p.WEB", TitleClean = "Movie", Year = 2024, MediaType = "movie", CategoryId = 900 };
        Assert.Null(PosterFetchService.ResolveVideoType(row, UnifiedCategory.Animation));
        row.Year = 2025; row.TitleClean = "Other";
        Assert.Null(PosterFetchService.ResolveVideoType(row, UnifiedCategory.Animation));
        row.TitleClean = "Movie"; row.MediaType = "tv";
        Assert.Null(PosterFetchService.ResolveVideoType(row, UnifiedCategory.Animation));
        row.MediaType = "movie"; row.Season = 1;
        Assert.Equal("tv", PosterFetchService.ResolveVideoType(row, UnifiedCategory.Animation));
    }

    [Theory]
    [InlineData(UnifiedCategory.Film)]
    [InlineData(UnifiedCategory.Animation)]
    [InlineData(UnifiedCategory.Spectacle)]
    public async Task IncompatibleTvdbClaimDoesNotDisableMovieYearCollection(UnifiedCategory category)
    {
        var context = new CandidateRankingContext(SearchCandidateGenerator.Generate(new("Movie.2025.1080p.WEB", category)),
            category, SearchMediaType.Movie, RssEvidence: new RssMatchingEvidence(1, null, 123, null, null));
        var result = await MovieYearSearchCollector.CollectAsync(context, new[] { "fr-FR", "en-US" }, (_, _, _, year, _) => {
            Assert.NotNull(year);
            TmdbClient.SearchResult[] rows = year == 2025 ? [new(1, "Movie", "Movie", null, "movie", 2025, "en")] : [];
            return Task.FromResult<TmdbClient.VideoMatchingPage?>(new(rows, rows.Length, rows.Length));
        }, default);
        Assert.Equal("targeted_year", result.SearchMode);
        Assert.True(result.Complete);
        Assert.Equal(6, result.Counters.SearchRequests);
    }
}
