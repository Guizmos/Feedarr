using System.Text.Json;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Tests;

public sealed class EpisodeCalendarTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Theory]
    [InlineData("different_dates")]
    [InlineData("same_dates")]
    [InlineData("unverified")]
    [InlineData("different_episode")]
    [InlineData("homonym")]
    [InlineData("unavailable")]
    public async Task CalendarConflictRequiresIndependentNumberedEpisodeProof(string scenario)
    {
        var queries = new[] { new SearchCandidate("Example", null, CandidateYearEvidence.None, SearchCandidateOrigin.Primary) };
        var candidates = new[] { new ProviderSearchCandidate(new("tmdb", SearchMediaType.Series, "1"), "Example", 2026) };
        Task<JsonElement?> Get(string provider, string path, CancellationToken ct)
        {
            JsonElement? value;
            if (path.StartsWith("search/")) value = scenario == "unavailable" ? null : Json(new[] { new { show = new { id = 2, name = "Example", premiered = "2026-01-01" } } });
            else if (provider == "tmdb" && path.Contains("/season/"))
                value = Json(scenario == "different_episode"
                    ? new Dictionary<string, object> { ["id"] = 10, ["show_id"] = 99, ["season_number"] = 1, ["episode_number"] = 1, ["air_date"] = "2026-01-01" }
                    : new Dictionary<string, object> { ["id"] = 10, ["season_number"] = 1, ["episode_number"] = scenario == "unverified" ? 2 : 1, ["air_date"] = "2026-01-01" });
            else if (provider == "tmdb") value = Json(new { id = 1, first_air_date = "2026-01-01", external_ids = new { tvdb_id = 100 }, seasons = new[] { new { season_number = 1, episode_count = 10 } } });
            else if (path.EndsWith("/episodes")) value = Json(new[] { new { id = 20, season = 1, number = scenario == "different_episode" ? 2 : 1, airdate = scenario == "same_dates" ? "2026-01-01" : "2026-01-02", _links = new { show = new { href = "https://api.tvmaze.com/shows/2" } } } });
            else value = Json(new { id = 2, premiered = "2026-01-01", name = "Example", externals = new { thetvdb = scenario == "homonym" ? 200 : 100 } });
            return Task.FromResult(value);
        }
        Task<ProviderSearchCandidate[]> Enrich() => TelevisionCandidates.EnrichAsync(queries, UnifiedCategory.Serie, "Example.S01E01", candidates, Get, true, default);
        if (scenario is "unavailable" or "different_episode")
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(Enrich);
            Assert.Equal(scenario == "unavailable" ? "tvmaze_search_coverage_unavailable" : scenario == "different_episode" ? "tv_episode_identity_conflict" : "tv_episode_date_conflict", error.Message);
            return;
        }
        var rows = await Enrich();
        var result = SearchCandidateRanker.Rank(new(queries, UnifiedCategory.Serie, SearchMediaType.Series, Season: 1, Episode: 1), rows);
        if (scenario == "homonym")
        {
            Assert.Equal(2, rows.Length);
            Assert.Empty(rows[0].KnownExternalIdentities ?? Array.Empty<SearchIdentity>());
            Assert.NotEqual(CandidateDecision.Accept, result.Decision);
        }
        else
        {
            Assert.Single(rows);
            Assert.Equal(CandidateDecision.Accept, result.Decision);
            if (scenario == "different_dates")
            {
                Assert.Null(rows[0].Television!.AirDate);
                Assert.Equal(2, rows[0].Television!.AirDates!.Count);
                Assert.Equal(2, rows[0].Television!.EpisodeIdentities!.Count);
                var byDate = SearchCandidateRanker.Rank(new(queries, UnifiedCategory.Emission, SearchMediaType.Series, AirDate: "2026-01-01"), rows);
                Assert.NotEqual(CandidateDecision.Accept, byDate.Decision);
            }
        }
    }
}
