using System.Text.Json;
using Feedarr.Api.Controllers;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Tmdb;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Feedarr.Api.Tests;

public sealed class MatchingShadowDiagnosticsTests
{
    private static MatchingShadowObservation Observation(long id = 1, string? decision = "Accept", string historicalId = "100")
    {
        var history = new HistoricalShadowDecision("Accept", new("tmdb", SearchMediaType.Movie, historicalId), 1);
        var current = new MatchingShadowState { Id = id, RawTitle = "The.Matrix.1999.BluRay.x264-GRP", Category = "film" };
        var result = MatchingShadowEvaluator.Observe(true, current, () => current,
            new("The Matrix", 1999, new[] { new TmdbClient.SearchResult(100, "The Matrix", null, "/ignored", "movie", 1999, "en") }, true), history)!;
        return decision is null ? result with { Status = "not_evaluable", V2 = null } :
            result with { V2 = result.V2! with { Decision = Enum.Parse<CandidateDecision>(decision), SelectedIdentity = decision == "Accept" ? result.V2!.SelectedIdentity : null } };
    }

    [Fact]
    public void AlwaysStartsOff_AndDoesNotRetainStateAcrossInstances()
    {
        var first = new MatchingShadowDiagnostics();
        Assert.Null(first.TryBegin());
        Assert.False(first.Snapshot().Enabled);
        first.SetEnabled(true);
        Assert.NotNull(first.TryBegin());
        Assert.False(new MatchingShadowDiagnostics().Snapshot().Enabled);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("reset")]
    [InlineData("stop-and-start")]
    public void InFlightResults_CannotSurviveStopOrReset(string action)
    {
        var diagnostics = new MatchingShadowDiagnostics();
        diagnostics.SetEnabled(true);
        var generation = diagnostics.TryBegin()!.Value;
        if (action == "reset") diagnostics.Reset();
        else diagnostics.SetEnabled(false);
        if (action == "stop-and-start") diagnostics.SetEnabled(true);
        diagnostics.Publish(generation, Observation(), "The Matrix", null, 2);
        Assert.Empty(diagnostics.Snapshot().Entries);
        Assert.False(diagnostics.IsCurrent(generation));
    }

    [Fact]
    public void DelayedEnable_CannotUndoStop_EvenWhenAlreadyOff()
    {
        var diagnostics = new MatchingShadowDiagnostics();
        var expected = diagnostics.Snapshot().Generation;
        diagnostics.SetEnabled(false);
        Assert.False(diagnostics.SetEnabled(true, expected).Enabled);
        Assert.True(diagnostics.SetEnabled(true, diagnostics.Snapshot().Generation).Enabled);
    }

    [Fact]
    public void CountsDivergencesDurationAndDistinctReleases_OverBoundedWindow()
    {
        var diagnostics = new MatchingShadowDiagnostics();
        diagnostics.SetEnabled(true);
        var generation = diagnostics.TryBegin()!.Value;
        diagnostics.Publish(generation, Observation(), "The Matrix", null, 2);
        diagnostics.Publish(generation, Observation(decision: "Reject"), "The Matrix", null, 4);
        diagnostics.Publish(generation, Observation(2, "Ambiguous"), "The Matrix", null, 6);
        diagnostics.Publish(generation, Observation(3, null), "The Matrix", null, 8);
        diagnostics.Publish(generation, Observation(4, historicalId: "999"), "The Matrix", null, 10);
        var snapshot = diagnostics.Snapshot();
        Assert.Equal(5, snapshot.Observations);
        Assert.Equal(4, snapshot.Releases);
        Assert.Equal(4, snapshot.Evaluated);
        Assert.Equal(3, snapshot.EvaluatedReleases);
        Assert.Equal(2, snapshot.Accept);
        Assert.Equal(1, snapshot.Ambiguous);
        Assert.Equal(1, snapshot.Reject);
        Assert.Equal(1, snapshot.NotEvaluable);
        Assert.Equal(3, snapshot.Divergences);
        Assert.Equal(6, snapshot.AverageDurationMs);
        Assert.All(snapshot.Entries, entry => Assert.True(double.IsFinite(entry.DurationMs)));
        Assert.NotEmpty(snapshot.Entries[0].Queries);
        Assert.Equal("Parsed", snapshot.Entries[0].Queries[0].YearEvidence);
        Assert.Equal("movie", snapshot.Entries[0].Candidates[0].Identity.Type);
    }

    [Fact]
    public async Task ConcurrentPublicationResetAndToggle_RemainsBoundedAndStops()
    {
        var diagnostics = new MatchingShadowDiagnostics();
        diagnostics.SetEnabled(true);
        var observation = Observation();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                if (i % 73 == 0) diagnostics.Reset();
                if (i % 37 == 0) diagnostics.SetEnabled(i % 2 == 0);
                var token = diagnostics.TryBegin();
                if (token.HasValue) diagnostics.Publish(token.Value, observation, "The Matrix", null, 1);
                var snapshot = diagnostics.Snapshot();
                Assert.InRange(snapshot.Observations, 0, MatchingShadowDiagnostics.Capacity);
                Assert.Equal(snapshot.Observations, snapshot.Accept + snapshot.Ambiguous + snapshot.Reject + snapshot.NotEvaluable);
            }
        })));
        diagnostics.SetEnabled(true);
        var generation = diagnostics.TryBegin()!.Value;
        for (var i = 0; i < 100; i++) diagnostics.Publish(generation, observation with { ReleaseId = i }, "The Matrix", null, 1);
        Assert.Equal(64, diagnostics.Snapshot().Observations);
        Assert.Equal(99, diagnostics.Snapshot().Entries[0].ReleaseId);
        diagnostics.SetEnabled(false);
        diagnostics.Publish(generation, observation, "The Matrix", null, 1);
        Assert.Equal(99, diagnostics.Snapshot().Entries[0].ReleaseId);
        Assert.Empty(diagnostics.Reset().Entries);
    }

    [Theory]
    [InlineData("Movie https://tracker.example/api?apikey=secret-value")]
    [InlineData("Movie [www.tracker.fr]")]
    [InlineData("Movie bearer SECRET")]
    [InlineData("Movie /volume1/private")]
    [InlineData("user@example.org")]
    public void SensitiveTitles_AreFullyRedacted(string text)
    {
        var diagnostics = new MatchingShadowDiagnostics();
        diagnostics.SetEnabled(true);
        diagnostics.Publish(diagnostics.TryBegin()!.Value, Observation(), text, null, 1);
        var json = JsonSerializer.Serialize(diagnostics.Snapshot());
        Assert.DoesNotContain(text, json);
        Assert.Contains("titre masqu", json);
        Assert.DoesNotContain("secret-value", json);
    }

    [Fact]
    public void ApiProjection_SanitizesCandidateAndQueryTitles_AndOmitsProviderUrls()
    {
        var diagnostics = new MatchingShadowDiagnostics();
        diagnostics.SetEnabled(true);
        var observation = Observation() with { Queries = new[] { new SearchCandidate("https://private/?token=secret-value", null, CandidateYearEvidence.None, SearchCandidateOrigin.Primary) } };
        var batch = new MatchingShadowBatch("unused", null, new[] { new TmdbClient.SearchResult(100, "https://private/?token=secret-value", null,
            "https://private/poster?token=secret-value", "movie", 1999, "en") }, true);
        diagnostics.Publish(diagnostics.TryBegin()!.Value, observation, "The Matrix", batch, 1);
        var json = JsonSerializer.Serialize(diagnostics.Snapshot());
        Assert.DoesNotContain("secret-value", json);
        Assert.DoesNotContain("https://private", json);
        Assert.DoesNotContain("PosterPath", json);
        Assert.Contains("titre masqu", json);
    }

    [Fact]
    public void SafeTitles_KeepTheirContent_ButAreBoundedAndNotHtmlRendered()
    {
        Assert.Equal("The Matrix", MatchingShadowDiagnostics.SafeText("The Matrix"));
        Assert.Equal("AB", MatchingShadowDiagnostics.SafeText("A\nB"));
        Assert.Equal(512, MatchingShadowDiagnostics.SafeText(new string('A', 10000)).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Api_RequiresExistingAuthentication_AndNeverCaches(bool authenticated)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        var context = new DefaultHttpContext();
        context.Items[BasicAuthMiddleware.AuthPassedKey] = authenticated;

        var controller = new MatchingShadowController(rig.Posters) { ControllerContext = new() { HttpContext = context } };
        var allowed = authenticated;
        Assert.Equal(allowed, controller.Get() is OkObjectResult);
        Assert.Equal(allowed, controller.Set(new(true, rig.ShadowDiagnostics.Snapshot().Generation)) is OkObjectResult);
        Assert.Equal(allowed, rig.ShadowDiagnostics.Snapshot().Enabled);
        Assert.Equal(allowed, controller.Reset() is OkObjectResult);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Empty(rig.TmdbSearchRequests);
    }

    [Fact]
    public void EnableRequiresFreshGeneration_AndOffIsUnconditional()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        var context = new DefaultHttpContext();
        context.Items[BasicAuthMiddleware.AuthPassedKey] = true;
        var controller = new MatchingShadowController(rig.Posters) { ControllerContext = new() { HttpContext = context } };
        Assert.IsType<BadRequestObjectResult>(controller.Set(new(true)));
        var generation = rig.ShadowDiagnostics.Snapshot().Generation;
        controller.Set(new(false));
        controller.Set(new(true, generation));
        Assert.False(rig.ShadowDiagnostics.Snapshot().Enabled);
        controller.Set(new(true, rig.ShadowDiagnostics.Snapshot().Generation));
        Assert.True(rig.ShadowDiagnostics.Snapshot().Enabled);
        controller.Set(new(false));
        Assert.False(rig.ShadowDiagnostics.Snapshot().Enabled);
    }

    [Fact]
    public async Task ApiMutations_InheritCrossOriginProtection()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "PUT";
        context.Request.Path = "/api/diagnostics/matching-shadow";
        context.Request.Scheme = "https";
        context.Request.Host = new("feedarr.local");
        context.Request.Headers.Origin = "https://untrusted.example";
        var reached = false;
        var middleware = new AntiCsrfOriginMiddleware(_ => { reached = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(context, new ConfigurationBuilder().Build(), NullLogger<AntiCsrfOriginMiddleware>.Instance);
        Assert.False(reached);
        Assert.Equal(403, context.Response.StatusCode);
    }

    [Fact]
    public async Task LiveSwitch_ChangesOnlyDiagnostics_NotStoredAssociationsOrProviderRequests()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true);
        var id = rig.IngestRelease("The.Matrix.1999.BluRay.x264-GRP", UnifiedCategory.Film);
        var before = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(rig.DatabasePath));
        rig.ShadowDiagnostics.SetEnabled(true);
        rig.ShadowDiagnostics.Reset();
        rig.ShadowDiagnostics.SetEnabled(false);
        Assert.Equal(before, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(rig.DatabasePath)));
        Assert.Empty(rig.TmdbSearchRequests);
        rig.ShadowDiagnostics.SetEnabled(true);
        await rig.FetchAsync(id);
        Assert.Single(rig.ShadowDiagnostics.Snapshot().Entries);
        Assert.Equal(100, rig.GetReleaseForPoster(id)!.TmdbId);
        var state = rig.Repository.ReadMatchingShadowState(id);
        var requests = rig.TmdbSearchRequests.Count;
        rig.ShadowDiagnostics.SetEnabled(false);
        await rig.FetchAsync(id);
        Assert.Single(rig.ShadowDiagnostics.Snapshot().Entries);
        Assert.Equal(requests, rig.TmdbSearchRequests.Count);
        Assert.Equal(state, rig.Repository.ReadMatchingShadowState(id));
    }
}
