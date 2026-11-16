using System.Text.Json;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Matching.Replay;

namespace Feedarr.Api.Tests;

public sealed class RankingCorpusTests
{
    private static MatchingShadowObservation Observe(string title, bool empty = false)
    {
        var context = new CandidateRankingContext(new[] { new SearchCandidate(title, 2009, CandidateYearEvidence.Parsed, SearchCandidateOrigin.Primary) }, UnifiedCategory.Film, SearchMediaType.Movie);
        var candidates = empty ? Array.Empty<ProviderSearchCandidate>() : new[] { new ProviderSearchCandidate(new("tmdb", SearchMediaType.Movie, "1"), title, 2009) };
        return new(1, "evaluated", new("stored_identity_only", null, null), SearchCandidateRanker.Rank(context, candidates), Array.Empty<string>(), false) {
            Queries = context.Queries, RankingContext = context, ProviderCandidates = candidates };
    }
    [Fact]
    public void ExportReplaysSameDecisionWithoutChangingObservation()
    {
        var observation = Observe("Example");
        var input = RankingCorpus.Create(observation)!.Value;
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(input.GetRawText()) <= RankingCorpus.MaximumBytes);
        var replay = RankingCorpus.Replay(input);
        Assert.Equal(observation.V2!.Decision, replay.Decision);
        Assert.Equal(observation.V2.SelectedIdentity, replay.SelectedIdentity);
        Assert.Equal("no_candidates", MatchingDiagnosticProjection.Project(Observe("Example", true), "Example", 1).Outcome);
        Assert.Equal("accept", MatchingDiagnosticProjection.Project(observation, "Example", 1).Outcome);
    }
    [Theory]
    [InlineData("https://private/?apikey=secret")]
    [InlineData("Title passkey=abc")]
    [InlineData("Title /private/file")]
    public void SensitiveInputIsOmittedRatherThanAltered(string title) => Assert.Null(RankingCorpus.Create(Observe(title)));

    [Fact]
    public void BoundsAndExplicitSelectionAreRequired()
    {
        Assert.Null(RankingCorpus.Create(Observe(new string('a', 513))));
        Assert.Throws<ArgumentException>(() => ReplaySelectionRules.Normalize(new(CaptureRankingInput: true)));
        Assert.Throws<ArgumentException>(() => ReplaySelectionRules.Normalize(new(Count: 25, Selection: new("ids", ReleaseIds: Enumerable.Range(1, 21).Select(i => (long)i).ToArray()), CaptureRankingInput: true)));
    }
    [Fact]
    public async Task ConcurrentExportsRemainIndependent()
    {
        var exports = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() => RankingCorpus.Create(Observe("Title " + i))!.Value)));
        Assert.Equal(10, exports.Select(p => p.GetProperty("Context").GetRawText()).Distinct().Count());
    }
    [Fact]
    public void RejectedCandidatesAndIncompleteCoverageAreNotEmptySearches()
    {
        var observation = Observe("Example");
        var candidates = new[] { observation.ProviderCandidates[0] with { Title = "Different Work" } };
        var rejected = observation with { ProviderCandidates = candidates, V2 = SearchCandidateRanker.Rank(observation.RankingContext!, candidates) };
        Assert.Equal("candidates_rejected", MatchingDiagnosticProjection.Project(rejected, "Example", 1).Outcome);
        Assert.Equal("coverage_incomplete", MatchingDiagnosticProjection.Project(observation with { V2 = null }, "Example", 1).Outcome);
        Assert.Equal(observation.V2!.Decision, SearchCandidateRanker.Rank(observation.RankingContext!, observation.ProviderCandidates).Decision);
    }
}
