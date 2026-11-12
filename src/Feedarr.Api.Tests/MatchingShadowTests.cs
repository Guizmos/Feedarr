using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Tests;

public sealed class MatchingShadowTests
{
    private static MatchingShadowState State => new() { Id = 1, RawTitle = "The.Matrix.1999.BluRay.x264-GRP", TitleClean = "The Matrix", Year = 1999, Category = "film" };
    private static TmdbClient.SearchResult Film(int id = 100, int year = 1999, string type = "movie")
        => new(id, "The Matrix", "The Matrix", null, type, year, "en");
    private static HistoricalShadowDecision Historical => new("Accept", new("tmdb", SearchMediaType.Movie, "100"), 1);
    private static MatchingShadowBatch Batch(params TmdbClient.SearchResult[] results) => new("The Matrix", 1999, results, true);
    private static MatchingShadowObservation Observe(MatchingShadowBatch? batch, MatchingShadowState? state = null)
    {
        var current = state ?? State;
        return MatchingShadowEvaluator.Observe(true, current, () => current, batch, Historical)!;
    }

    [Fact]
    public void Disabled_DoesNotEvenReadState()
        => Assert.Null(MatchingShadowEvaluator.Observe(false, State, () => throw new Exception("must not read"), null, Historical));

    [Theory]
    [InlineData(1999, "movie", CandidateDecision.Accept)]
    [InlineData(1980, "movie", CandidateDecision.Reject)]
    [InlineData(1999, "tv", CandidateDecision.Reject)]
    public void CompleteResults_AreRankedIndependentlyOfHistoricalChoice(int year, string type, object decisionValue)
    {
        var result = Observe(Batch(Film(year: year, type: type)));
        Assert.Equal("evaluated", result.Status);
        Assert.Equal((CandidateDecision)decisionValue, result.V2!.Decision);
        Assert.Equal(Historical, result.Historical);
        Assert.False(result.PreserveExisting);
    }

    [Fact]
    public void CompetingIdentities_AreAmbiguous()
    {
        var result = Observe(Batch(Film(), Film(101)));
        Assert.Equal(CandidateDecision.Ambiguous, result.V2!.Decision);
        Assert.Null(result.V2.SelectedIdentity);
    }

    [Fact]
    public void EmptyCompleteResponse_IsEvaluableRejection()
        => Assert.Equal(CandidateDecision.Reject, Observe(Batch()).V2!.Decision);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrPartialResponse_IsNotAnEmptyResult(bool partial)
    {
        var result = Observe(partial ? Batch(Film()) with { CoverageComplete = false } : null);
        Assert.Equal("not_evaluable", result.Status);
        Assert.Null(result.V2);
        Assert.NotEmpty(result.MissingInformation);
    }

    [Fact]
    public void NumericTmdbId_DoesNotSupplyItsMissingType()
    {
        var result = Observe(Batch(Film(type: "")));
        Assert.Contains("provider_identity_type_or_id_missing", result.MissingInformation);
        Assert.Null(result.V2);
    }

    [Fact]
    public void UnrequestedHypothesis_RemainsNotEvaluable()
    {
        var result = Observe(new("Blade Runner 2049", null, new[] { Film() }, true),
            State with { RawTitle = "Blade.Runner.2049.1080p", TitleClean = "Blade Runner 2049", Year = null });
        Assert.Contains("generated_query_not_covered_by_historical_requests", result.MissingInformation);
        Assert.Null(result.V2);
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("title")]
    [InlineData("legacy")]
    [InlineData("tmdb")]
    [InlineData("unknown-tmdb")]
    [InlineData("tvdb")]
    [InlineData("external")]
    public void PersistedProtection_PreventsAnyV2Proposal(string protection)
    {
        var state = protection switch
        {
            "manual" => State with { ManualProvider = "tmdb" },
            "title" => State with { ManualTitle = true },
            "legacy" => State with { LegacyManualPoster = true },
            "tmdb" => State with { TmdbId = 100, TmdbType = "movie" },
            "unknown-tmdb" => State with { TmdbId = 100 },
            "tvdb" => State with { TvdbId = 100 },
            _ => State with { ExternalProvider = "igdb", ExternalId = "100" }
        };
        var result = Observe(Batch(Film()), state);
        Assert.Equal("not_evaluable", result.Status);
        Assert.True(result.PreserveExisting);
        Assert.Null(result.V2);
        if (protection == "unknown-tmdb") Assert.Contains("established_tmdb_type_unknown", result.MissingInformation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcurrentCorrectionBeforeOrDuringRanking_DiscardsProposal(bool during)
    {
        var calls = 0;
        var result = MatchingShadowEvaluator.Observe(true, State,
            () => ++calls == 1 && during ? State : State with { Revision = "corrected", ManualProvider = "tmdb" }, Batch(Film()), Historical)!;
        Assert.Equal("not_evaluable", result.Status);
        Assert.True(result.PreserveExisting);
        Assert.Null(result.V2);
    }

    [Fact]
    public void ReadFailure_CannotEscapeIntoHistoricalPipeline()
    {
        var result = MatchingShadowEvaluator.Observe(true, State, () => throw new InvalidOperationException(), Batch(Film()), Historical)!;
        Assert.Contains("shadow_evaluation_failed", result.MissingInformation);
        Assert.Equal(Historical, result.Historical);
    }

    [Fact]
    public void PosterPresence_HasNoInfluenceOnRanking()
    {
        var without = Observe(Batch(Film()));
        var with = Observe(Batch(Film() with { PosterPath = "/arbitrary.jpg" }));
        Assert.Equal(without.V2!.Decision, with.V2!.Decision);
        Assert.Equal(without.V2.SelectedIdentity, with.V2.SelectedIdentity);
        Assert.Equal(without.V2.Candidates[0].Confidence, with.V2.Candidates[0].Confidence);
    }

    [Fact]
    public async Task MultiplePages_AreNotTreatedAsCompleteCoverage()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit,
            matchingShadowEnabled: true, completeTmdbMetadata: true, tmdbTotalPages: 2);
        var id = rig.IngestRelease(State.RawTitle!, UnifiedCategory.Film);
        await rig.FetchAsync(id);
        var observation = Assert.Single(rig.ShadowObservations);
        Assert.Contains("provider_response_partial_or_coverage_unknown", observation.MissingInformation);
        Assert.Null(observation.V2);
        Assert.Equal(100, rig.GetReleaseForPoster(id)!.TmdbId);
    }

    [Fact]
    public void RepositoryReader_ObservesManualFlag_WithoutWritingTemporaryDatabase()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        var id = rig.IngestRelease(State.RawTitle!, UnifiedCategory.Film);
        Assert.False(rig.Repository.ReadMatchingShadowState(id)!.ManualTitle);
        rig.SetManualTitleForTest(id);
        var before = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(rig.DatabasePath));
        var walPath = rig.DatabasePath + "-wal";
        var walBefore = File.Exists(walPath) ? File.ReadAllBytes(walPath) : null;
        var current = rig.Repository.ReadMatchingShadowState(id)!;
        Assert.True(current.ManualTitle);
        var result = MatchingShadowEvaluator.Observe(true, current, () => rig.Repository.ReadMatchingShadowState(id), Batch(Film()), Historical)!;
        Assert.True(result.PreserveExisting);
        Assert.Null(result.V2);
        Assert.Equal(before, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(rig.DatabasePath)));
        Assert.Equal(walBefore, File.Exists(walPath) ? File.ReadAllBytes(walPath) : null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnabledShadow_PreservesHistoricalRequestsAndSelectedIdentity(bool complete)
    {
        using var off = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: complete);
        using var on = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, matchingShadowEnabled: true, completeTmdbMetadata: complete);
        var offId = off.IngestRelease(State.RawTitle!, UnifiedCategory.Film);
        var onId = on.IngestRelease(State.RawTitle!, UnifiedCategory.Film);
        var offResult = await off.FetchAsync(offId);
        var onResult = await on.FetchAsync(onId);
        Assert.Equal(offResult.Ok, onResult.Ok);
        Assert.Equal(offResult.StatusCode, onResult.StatusCode);
        var offStored = off.GetReleaseForPoster(offId)!;
        var onStored = on.GetReleaseForPoster(onId)!;
        Assert.Equal(offStored.TmdbId, onStored.TmdbId);
        Assert.Equal(offStored.TmdbMediaType, onStored.TmdbMediaType);
        Assert.Equal(offStored.TvdbId, onStored.TvdbId);
        Assert.Equal(offStored.PosterProvider, onStored.PosterProvider);
        Assert.Equal(offStored.ExtProvider, onStored.ExtProvider);
        Assert.Equal(offStored.ExtProviderId, onStored.ExtProviderId);
        Assert.Equal(offStored.TitleClean, onStored.TitleClean);
        Assert.Equal(off.TmdbSearchRequests.Select(u => u.AbsoluteUri), on.TmdbSearchRequests.Select(u => u.AbsoluteUri));
        Assert.Empty(off.ShadowObservations);
        var observation = Assert.Single(on.ShadowObservations);
        Assert.True(observation.Status == (complete ? "evaluated" : "not_evaluable"), string.Join(",", observation.MissingInformation));
        if (complete) Assert.Equal(CandidateDecision.Accept, observation.V2!.Decision);
        var state = on.Repository.ReadMatchingShadowState(onId);
        Assert.Equal(on.GetReleaseForPoster(onId)!.TmdbId, state!.TmdbId);
        Assert.Equal(state, on.Repository.ReadMatchingShadowState(onId));
        var searches = on.TmdbSearchRequests.Count;
        await on.FetchAsync(onId);
        Assert.Equal(searches, on.TmdbSearchRequests.Count);
        Assert.Equal(state, on.Repository.ReadMatchingShadowState(onId));
        Assert.All(on.ShadowObservations.Skip(1), o => Assert.Equal("not_evaluable", o.Status));
    }
}
