using System.Text.Json;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Posters;

namespace Feedarr.Api.Tests;

public sealed class TelevisionEnrichmentDiagnosticTests
{
    private static readonly SearchCandidate[] Queries = { new("Example", null, CandidateYearEvidence.None, SearchCandidateOrigin.Primary) };
    private static readonly ProviderSearchCandidate[] Candidates = { new(new("tmdb", SearchMediaType.Series, "1"), "Example", 2026) };
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    [Theory]
    [InlineData(null, "tvmaze_search_coverage_unavailable")]
    [InlineData("{}", "tvmaze_search_coverage_unavailable")]
    [InlineData("[{}]", "tvmaze_search_invalid")]
    public async Task UnavailableOrInvalidMazeNeverSilentlyDiscardsCompetitors(string? response, string invariant)
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => TelevisionCandidates.EnrichAsync(
            Queries, UnifiedCategory.Serie, "Example.S01E01", Candidates,
            (_, _, _) => Task.FromResult<JsonElement?>(response is null ? null : Json(response)), true, default));
        Assert.Equal(invariant, error.Message);
        Assert.Equal(invariant, VideoMatchingFailure.Classify(error, "enrichment").Invariant);
    }

    [Fact]
    public async Task MissingDetailsAndConflictingYearRemainDistinct()
    {
        foreach (var conflict in new[] { false, true })
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => TelevisionCandidates.EnrichAsync(
                Queries, UnifiedCategory.Serie, "Example.S01E01", Candidates,
                (_, _, _) => Task.FromResult<JsonElement?>(conflict ? Json("{\"id\":1,\"first_air_date\":\"1990-01-01\"}") : null), false, default));
            Assert.Equal(conflict ? "tv_provider_year_conflict" : "tv_details_identity_unavailable", error.Message);
            Assert.Equal(error.Message, VideoMatchingFailure.Classify(error, "enrichment").Invariant);
        }
    }

    [Fact]
    public async Task MissingEpisodeRemainsUnknownAndDoesNotEnableAcceptance()
    {
        var rows = await TelevisionCandidates.EnrichAsync(Queries, UnifiedCategory.Serie, "Example.S01E01", Candidates,
            (_, path, _) => Task.FromResult<JsonElement?>(path.Contains("/season/") ? null : Json("{\"id\":1,\"first_air_date\":\"2026-01-01\",\"seasons\":[]}")), false, default);
        Assert.Null(rows[0].Television!.EpisodeExists);
        var result = SearchCandidateRanker.Rank(new(Queries, UnifiedCategory.Serie, SearchMediaType.Series, Season: 1, Episode: 1), rows);
        Assert.NotEqual(CandidateDecision.Accept, result.Decision);
    }

    [Theory]
    [InlineData("tvmaze_search_invalid https://private/?key=secret")]
    [InlineData("tv_untrusted")]
    public void UntrustedExceptionMessagesAreNeverCopied(string message)
    {
        var result = VideoMatchingFailure.Classify(new InvalidDataException(message), "enrichment");
        Assert.Null(result.Invariant);
        Assert.DoesNotContain(message, result.ToString());
    }
}
