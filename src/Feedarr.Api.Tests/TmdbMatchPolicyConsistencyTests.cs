using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Tests;

public sealed class TmdbMatchPolicyConsistencyTests
{
    [Theory]
    [InlineData(2012, 1990, "movie", UnifiedCategory.Film, false, "release_year_conflict")]
    [InlineData(2012, 2012, "series", UnifiedCategory.Film, false, "media_type_conflict")]
    [InlineData(2012, 2012, "movie", UnifiedCategory.Serie, false, "media_type_conflict")]
    [InlineData(2012, 2012, "movie", UnifiedCategory.Film, true, "accepted_strong")]
    [InlineData(2012, 2011, "movie", UnifiedCategory.Film, true, "accepted_strong")]
    [InlineData(2012, 2013, "movie", UnifiedCategory.Film, true, "accepted_strong")]
    [InlineData(2012, 2014, "movie", UnifiedCategory.Film, false, "release_year_conflict")]
    [InlineData(null, 1990, "movie", UnifiedCategory.Film, true, "accepted_strong")]
    [InlineData(2012, null, "movie", UnifiedCategory.Film, true, "accepted_strong")]
    public void HighScore_DoesNotOverrideReliableContradictions(int? year, int? candidateYear, string type, UnifiedCategory category, bool accepted, string reason)
    {
        const string title = "The French Dispatch";
        var candidate = new TmdbClient.SearchResult(123, title, title, "/poster.jpg", type, candidateYear, "en");
        var decision = TmdbMatchPolicy.Evaluate(title, year, category,
            TitleAmbiguityEvaluator.Evaluate(title, type, year), candidate, 1.0f, true, 0.62f, 0.75f);
        Assert.Equal(accepted, decision.Accepted); Assert.Equal(reason, decision.Reason);
    }

    [Theory]
    [InlineData(2024, 1999, 5, 1, true)]
    [InlineData(2024, 1999, null, 5, true)]
    [InlineData(2024, 1999, null, null, false)]
    [InlineData(2024, 1999, 1, 1, false)]
    [InlineData(2010, 2024, 5, 1, false)]
    public void EpisodeYear_IsNotAssumedToBePremiereYear(int queryYear, int premiere, int? season, int? episode, bool accepted)
    {
        const string title = "Long Running Show";
        var candidate = new TmdbClient.SearchResult(123, title, null, "/poster.jpg", "series", premiere, "en");
        var decision = TmdbMatchPolicy.Evaluate(title, queryYear, UnifiedCategory.Serie,
            TitleAmbiguityEvaluator.Evaluate(title, "series", queryYear), candidate, 0.96f, true, 0.62f, 0.75f, season, episode);
        Assert.Equal(accepted, decision.Accepted);
        if (accepted) Assert.Equal(TmdbYearRelation.EpisodeAfterPremiere, decision.YearRelation);
    }

    [Fact]
    public void CommonTitle_StillRequiresExactYear_AndWeakMatchCannotUseApproximateYear()
    {
        var candidate = new TmdbClient.SearchResult(123, "Red", null, "/poster.jpg", "movie", 2011, "en");
        Assert.False(TmdbMatchPolicy.IsAcceptable("Red", 2012, UnifiedCategory.Film,
            TitleAmbiguityEvaluator.Evaluate("Red", "movie", 2012), candidate, 1f, true, 0.62f, 0.75f));
        candidate = candidate with { Title = "Long Movie Title" };
        Assert.False(TmdbMatchPolicy.IsAcceptable(candidate.Title, 2012, UnifiedCategory.Film,
            TitleAmbiguityEvaluator.Evaluate(candidate.Title, "movie", 2012), candidate, 0.4f, true, 0.62f, 0.75f));
    }
}
