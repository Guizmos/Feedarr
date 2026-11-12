using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Matching.Replay;
using Feedarr.Api.Services.Resilience;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Feedarr.Api.Tests;

public sealed class MatchingReplayTests
{
    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Film(HttpRequestMessage request)
    {
        var localized = QueryHelpers.ParseQuery(request.RequestUri!.Query).ContainsKey("language");
        return Json(JsonSerializer.Serialize(new { page = 1, total_pages = 1, total_results = 1,
            results = new[] { new { id = 1184918, title = localized ? "Le Robot sauvage" : "The Wild Robot", original_title = "The Wild Robot", release_date = "2024-09-01", original_language = "en" } } }));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private sealed class Factory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null) : IHttpClientFactory
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal int Created { get; private set; }
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("tmdb-matching-replay", name); Created++;
            return new(new ReplayBudgetHandler { InnerHandler = new Handler((request, ct) =>
            {
                Interlocked.Increment(ref _calls);
                return send?.Invoke(request, ct) ?? Task.FromResult(Film(request));
            }) });
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly PosterMatchingContractTestRig Rig = new(PosterContractScenario.MovieTmdbHit);
        internal readonly TestHostApplicationLifetime Lifetime = new();
        internal readonly MatchingShadowDiagnostics Shadow = new();
        internal readonly Factory Factory;
        internal readonly MatchingReplayService Replay;
        internal Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null)
        { Factory = new(send); Replay = new(Rig.ReplayReader, Factory, Shadow, Lifetime); }
        internal long Seed(string title = "Le.Robot.sauvage.2024.BluRay.x264-GRP", UnifiedCategory category = UnifiedCategory.Film)
            => Rig.IngestRelease(title, category);
        internal void Start(string mode = "offline", int budget = 30, int count = 10)
            => Replay.Start(new(count, mode, budget, Replay.Snapshot().Generation));
        internal async Task<ReplaySnapshot> Done()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (Replay.Snapshot().Running) await Task.Delay(10, timeout.Token);
            return Replay.Snapshot();
        }
        internal string DiskSnapshot()
        {
            var root = Path.GetDirectoryName(Rig.DatabasePath)!;
            return string.Join("\n", Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith("-shm"))
                .OrderBy(p => p, StringComparer.Ordinal).Select(p => Path.GetRelativePath(root, p) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))));
        }
        public void Dispose() { Replay.Stop(); SpinWait.SpinUntil(() => !Replay.Snapshot().Running, TimeSpan.FromSeconds(5)); Replay.Dispose(); Lifetime.Dispose(); Rig.Dispose(); }
    }

    [Fact]
    public void Sampling_IsDeterministicBalancedAndBounded()
    {
        var seeds = Enumerable.Range(1, 300).Select(i => new MatchingReplayReader.Seed
        { Id = i, Category = i % 2 == 0 ? "films" : "series", SourceId = i % 3, HasPoster = i % 4 == 0, HasIdentity = i % 5 == 0, RawTitle = "Le.Titre.2024.1080p" }).ToArray();
        var first = MatchingReplayReader.Sample(seeds, 100);
        Assert.Equal(first.Select(s => s.Id), MatchingReplayReader.Sample(seeds.Reverse(), 100).Select(s => s.Id));
        Assert.Equal(100, first.Count);
        Assert.Equal(2, first.Select(s => s.Category).Distinct().Count());
        Assert.Equal(3, first.Select(s => s.SourceId).Distinct().Count());
        Assert.Contains(first, s => s.HasPoster); Assert.Contains(first, s => !s.HasPoster);
        Assert.Contains(first, s => s.HasIdentity); Assert.Contains(first, s => !s.HasIdentity);
        Assert.Equal(200, MatchingReplayReader.Sample(seeds, 1000).Count);
    }

    [Fact]
    public async Task OfflineExistingReleases_NoNetwork_NoDatabaseOrPosterChanges()
    {
        using var fixture = new Fixture();
        for (var i = 0; i < 15; i++) fixture.Seed($"Film.{i}.2024.BluRay.x264-GRP");
        fixture.Seed("Game.v1.2-CODEX", UnifiedCategory.JeuWindows);
        var poster = Path.Combine(Path.GetDirectoryName(fixture.Rig.DatabasePath)!, "posters");
        Directory.CreateDirectory(poster); File.WriteAllText(Path.Combine(poster, "sentinel.png"), "temporary-test-image");
        var before = fixture.DiskSnapshot();
        fixture.Start(); var report = await fixture.Done();
        Assert.Equal("completed", report.Status); Assert.Equal(10, report.Processed);
        Assert.Equal(0, fixture.Factory.Created); Assert.Equal(0, report.RequestsUsed);
        Assert.Equal(10, report.Summary.NotEvaluable);
        Assert.All(report.Entries, e => Assert.Equal("offline_provider_responses_unavailable", e.Coverage));
        Assert.Equal(before, fixture.DiskSnapshot());
        Assert.False(fixture.Shadow.Snapshot().Enabled);
    }

    [Fact]
    public async Task LiveMultilingualDiagnostic_NoBusinessWrites_AndNoImplicitIdentityValidation()
    {
        using var fixture = new Fixture(); fixture.Seed();
        var before = fixture.DiskSnapshot();
        fixture.Start("tmdb"); var report = await fixture.Done();
        var entry = Assert.Single(report.Entries);
        Assert.Equal("accept", entry.Detail.Decision); Assert.Equal(2, report.RequestsUsed);
        Assert.Equal(2, fixture.Factory.Calls); Assert.Equal("complete", entry.Coverage);
        Assert.Equal("1184918", entry.Detail.SelectedIdentity!.Id); Assert.Equal("movie", entry.Detail.SelectedIdentity.Type);
        Assert.Equal("unknown", entry.IdentityVerified); Assert.Null(entry.DecisionDifference); Assert.Null(entry.IdentityDifference);
        Assert.Contains(entry.Detail.Candidates[0].OfficialTitles!, t => t.Title == "Le Robot sauvage" && t.Language == "fr-FR");
        Assert.Equal(before, fixture.DiskSnapshot()); Assert.Empty(fixture.Shadow.Snapshot().Entries);
    }

    [Fact]
    public async Task ManualProtectedIdentity_IsDiagnosticOnly_AndIsNeverUsedAsQuery()
    {
        var queries = new List<string>();
        using var fixture = new Fixture((request, _) => { queries.Add(QueryHelpers.ParseQuery(request.RequestUri!.Query)["query"].ToString()); return Task.FromResult(Film(request)); });
        var id = fixture.Seed();
        using (var connection = fixture.Rig.Database.Open()) connection.Execute("""
            UPDATE media_entities SET tmdb_id=999,tmdb_media_type='movie',manual_match_provider='tmdb',manual_match_id='999',manual_match_type='movie',match_revision='manual-test'
            WHERE id=(SELECT entity_id FROM releases WHERE id=@id)
            """, new { id });
        var before = fixture.DiskSnapshot();
        fixture.Start("tmdb"); var entry = Assert.Single((await fixture.Done()).Entries);
        Assert.True(entry.Protected); Assert.True(entry.Detail.PreserveExisting);
        Assert.True(entry.IdentityDifference); Assert.Equal("999", entry.Detail.HistoricalIdentity!.Id);
        Assert.All(queries, q => Assert.Equal("Le Robot sauvage", q));
        Assert.Equal(before, fixture.DiskSnapshot());
    }

    [Fact]
    public async Task UnknownStoredTmdbType_IsNotInferredFromNumericId()
    {
        using var fixture = new Fixture(); var id = fixture.Seed();
        using (var connection = fixture.Rig.Database.Open()) connection.Execute("UPDATE media_entities SET tmdb_id=999,tmdb_media_type=NULL WHERE id=(SELECT entity_id FROM releases WHERE id=@id)", new { id });
        fixture.Start("tmdb"); var entry = Assert.Single((await fixture.Done()).Entries);
        Assert.Null(entry.Detail.HistoricalIdentity); Assert.Null(entry.IdentityDifference); Assert.True(entry.Protected);
    }

    [Fact]
    public async Task OfflineCanReuseOnlyCompleteMatchingShadowSnapshot_ForExactUnchangedState()
    {
        using var fixture = new Fixture(); var id = fixture.Seed();
        var state = fixture.Rig.ReplayReader.Read(id)!;
        var observation = MatchingShadowEvaluator.Observe(true, state, () => state,
            new("Le Robot sauvage", 2024, new[] { new Feedarr.Api.Services.Tmdb.TmdbClient.SearchResult(1184918, "Le Robot sauvage", "The Wild Robot", null, "movie", 2024, "en") }, true),
            new("Reject", null, null))!;
        fixture.Shadow.SetEnabled(true);
        fixture.Shadow.Publish(fixture.Shadow.TryBegin()!.Value, observation, state.RawTitle, null, 1);
        fixture.Start(); var entry = Assert.Single((await fixture.Done()).Entries);
        Assert.Equal("accept", entry.Detail.Decision); Assert.True(entry.DecisionDifference); Assert.Equal(0, fixture.Factory.Calls);
        using (var connection = fixture.Rig.Database.Open()) connection.Execute("UPDATE releases SET year=1990 WHERE id=@id", new { id });
        fixture.Start(); entry = Assert.Single((await fixture.Done()).Entries);
        Assert.Equal("not_evaluable", entry.Detail.Decision);
        Assert.Equal("offline_provider_responses_unavailable", entry.Coverage);
        Assert.Single(fixture.Shadow.Snapshot().Entries);
    }

    [Fact]
    public async Task MissingCredentials_ConsumesNoHttpBudget()
    {
        using var fixture = new Fixture(); fixture.Seed();
        using (var connection = fixture.Rig.Database.Open()) connection.Execute("UPDATE external_provider_instances SET enabled=0 WHERE provider_key='tmdb'");
        var before = fixture.DiskSnapshot(); fixture.Start("tmdb"); var report = await fixture.Done();
        Assert.Equal(0, fixture.Factory.Calls); Assert.Equal(0, report.RequestsUsed);
        Assert.Equal("tmdb_not_configured", Assert.Single(report.Entries).Coverage);
        Assert.Equal(before, fixture.DiskSnapshot());
    }

    [Fact]
    public async Task BudgetCannotYieldAcceptanceFromIncompleteLanguageCoverage()
    {
        using var fixture = new Fixture(); fixture.Seed();
        fixture.Start("tmdb", budget: 1); var report = await fixture.Done();
        Assert.Equal(1, report.RequestsUsed); Assert.Equal(1, fixture.Factory.Calls);
        var entry = Assert.Single(report.Entries);
        Assert.Equal("http_budget_exhausted", entry.Coverage); Assert.Equal("not_evaluable", entry.Detail.Decision);
        Assert.Empty(entry.Detail.Candidates);
    }

    [Fact]
    public async Task BudgetCountsEveryFailedHttpAttempt_AndCannotExceedThirty()
    {
        var calls = 0;
        using var http = new HttpClient(new ReplayBudgetHandler { InnerHandler = new Handler((_, _) =>
        { Interlocked.Increment(ref calls); return Task.FromResult(Json("error", HttpStatusCode.InternalServerError)); }) });
        var budget = new ReplayHttpBudget(30, TimeSpan.Zero);
        for (var i = 0; i < 30; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.themoviedb.org/3/search/movie");
            request.Options.Set(ReplayHttpBudget.Option, budget);
            using var response = await http.SendAsync(request);
        }
        using var denied = new HttpRequestMessage(HttpMethod.Get, "https://api.themoviedb.org/3/search/movie"); denied.Options.Set(ReplayHttpBudget.Option, budget);
        await Assert.ThrowsAsync<ReplayBudgetExceededException>(() => http.SendAsync(denied));
        Assert.Equal(30, calls); Assert.Equal(30, budget.Used);
    }

    [Fact]
    public async Task RedirectHttpAttempts_UseTheSameBudget()
    {
        var calls = 0;
        using var http = new HttpClient(new ProtocolDowngradeRedirectHandler { InnerHandler = new ReplayBudgetHandler { InnerHandler = new Handler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://api.themoviedb.org/3/search/movie?page=2") } });
        }) } });
        var budget = new ReplayHttpBudget(2, TimeSpan.Zero);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.themoviedb.org/3/search/movie?page=1"); request.Options.Set(ReplayHttpBudget.Option, budget);
        await Assert.ThrowsAsync<ReplayBudgetExceededException>(() => http.SendAsync(request));
        Assert.Equal(2, calls); Assert.Equal(2, budget.Used);
    }

    [Theory]
    [InlineData("https://image.tmdb.org/3/search/movie")]
    [InlineData("https://api.themoviedb.org/3/movie/1/images")]
    [InlineData("http://api.themoviedb.org/3/search/movie")]
    public async Task DiagnosticTransport_DeniesOtherDestinationsAndPosterRoutes(string url)
    {
        using var http = new HttpClient(new ReplayBudgetHandler { InnerHandler = new Handler((_, _) => throw new Exception("must not send")) });
        using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Options.Set(ReplayHttpBudget.Option, new(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => http.SendAsync(request));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid-json")]
    [InlineData("partial")]
    [InlineData("timeout")]
    public async Task MissingPartialInvalidOrTimeout_AreNotEmptyResponses(string kind)
    {
        using var fixture = new Fixture((_, _) => kind switch
        {
            "missing" => Task.FromResult(Json("{\"results\":[]}")),
            "invalid-json" => Task.FromResult(Json("not json")),
            "timeout" => throw new TaskCanceledException("fake-private-url-must-not-be-exported"),
            _ => Task.FromResult(Json("{\"page\":1,\"total_pages\":1,\"total_results\":1,\"results\":[]}"))
        });
        fixture.Seed(); fixture.Start("tmdb"); var entry = Assert.Single((await fixture.Done()).Entries);
        Assert.Equal("not_evaluable", entry.Detail.Decision); Assert.Empty(entry.Detail.Candidates);
        Assert.DoesNotContain("fake-private-url", JsonSerializer.Serialize(entry));
    }

    [Fact]
    public async Task RateLimit_HaltsFurtherRequestsForTheSession()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json("", HttpStatusCode.TooManyRequests)));
        for (var i = 0; i < 10; i++) fixture.Seed();
        fixture.Start("tmdb"); var report = await fixture.Done();
        Assert.Equal(1, fixture.Factory.Calls); Assert.Equal(1, report.RequestsUsed);
        Assert.Equal(10, report.Processed); Assert.Equal(1, report.Errors);
        Assert.All(report.Entries, e => Assert.Equal("provider_rate_limited", e.Coverage));
        fixture.Replay.Stop(reset: true); fixture.Start("tmdb"); report = await fixture.Done();
        Assert.Equal(0, report.RequestsUsed); Assert.Equal(1, fixture.Factory.Calls);
        Assert.All(report.Entries, e => Assert.Equal("provider_rate_limit_cooldown", e.Coverage));
    }

    [Theory]
    [InlineData(500, 3)]
    [InlineData(401, 1)]
    public async Task SessionBudgetIncludesHttpErrors_WithoutAutomaticRetries(int status, int expectedCalls)
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json("error", (HttpStatusCode)status)));
        for (var i = 0; i < 10; i++) fixture.Seed();
        fixture.Start("tmdb", budget: 3); var report = await fixture.Done();
        Assert.Equal(expectedCalls, report.RequestsUsed); Assert.Equal(expectedCalls, fixture.Factory.Calls);
        Assert.Equal(expectedCalls, report.Errors); Assert.Equal(10, report.Summary.NotEvaluable);
    }

    [Fact]
    public async Task AmbiguousQueries_RequireCompleteCoverage_WithoutConfirmingHypotheticalYear()
    {
        using var fixture = new Fixture(); fixture.Seed("Blade.Runner.2049.1080p");
        fixture.Start("tmdb", budget: 3); var report = await fixture.Done();
        var entry = Assert.Single(report.Entries);
        Assert.Equal(3, report.RequestsUsed); Assert.Equal("http_budget_exhausted", entry.Coverage);
        Assert.Equal(2, entry.Detail.Queries.Count); Assert.Equal("Hypothesis", entry.Detail.Queries[1].YearEvidence);
        Assert.Null(entry.Detail.SelectedIdentity);
    }

    [Fact]
    public async Task ReplayNeverSendsConcurrentRequests()
    {
        var active = 0; var maximum = 0;
        using var fixture = new Fixture(async (request, ct) =>
        {
            var current = Interlocked.Increment(ref active); maximum = Math.Max(maximum, current);
            try { await Task.Delay(10, ct); return Film(request); }
            finally { Interlocked.Decrement(ref active); }
        });
        fixture.Seed(); fixture.Seed(); fixture.Start("tmdb", budget: 4);
        var report = await fixture.Done(); Assert.Equal(4, report.RequestsUsed); Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task Pagination_AllPagesAndLocalesAreRequired_AndBudgetIncludesThem()
    {
        using var fixture = new Fixture((request, _) =>
        {
            var query = QueryHelpers.ParseQuery(request.RequestUri!.Query); var page = int.Parse(query["page"]!);
            return Task.FromResult(Json(JsonSerializer.Serialize(new { page, total_pages = 2, total_results = 2,
                results = new[] { new { id = page, title = "Le Robot sauvage", original_title = "The Wild Robot", release_date = "2024-09-01" } } })));
        });
        fixture.Seed(); fixture.Start("tmdb"); var report = await fixture.Done();
        Assert.Equal(4, report.RequestsUsed); Assert.Equal("ambiguous", Assert.Single(report.Entries).Detail.Decision);
        fixture.Start("tmdb", budget: 3); report = await fixture.Done();
        Assert.Equal(3, report.RequestsUsed); Assert.Equal("not_evaluable", Assert.Single(report.Entries).Detail.Decision);
    }

    [Fact]
    public async Task CancellationAndReset_RejectDoubleStarts_AndPreventLatePublicationOrRequests()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (request, _) => { entered.TrySetResult(); await release.Task; return Film(request); });
        fixture.Seed(); fixture.Start("tmdb"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<InvalidOperationException>(() => fixture.Start());
        fixture.Replay.Stop(); Assert.Equal("stopped", fixture.Replay.Snapshot().Status);
        fixture.Replay.Stop(reset: true);
        Assert.Throws<InvalidOperationException>(() => fixture.Start());
        release.TrySetResult(); var report = await fixture.Done();
        Assert.Null(report.SessionId); Assert.Empty(report.Entries); Assert.Equal(1, fixture.Factory.Calls);
        fixture.Start(); report = await fixture.Done(); Assert.Equal(0, report.RequestsUsed);
    }

    [Fact]
    public async Task ConcurrentCorrection_InvalidatesTheDiagnosticProposal()
    {
        Fixture? fixture = null; long id = 0; var once = 0;
        using var scoped = new Fixture((request, _) =>
        {
            if (Interlocked.Increment(ref once) == 1)
                using (var connection = fixture!.Rig.Database.Open()) connection.Execute("UPDATE releases SET title_manual_override=1 WHERE id=@id", new { id });
            return Task.FromResult(Film(request));
        });
        fixture = scoped; id = fixture.Seed(); fixture.Start("tmdb");
        var entry = Assert.Single((await fixture.Done()).Entries);
        Assert.Equal("not_evaluable", entry.Detail.Decision); Assert.Null(entry.Detail.SelectedIdentity);
        Assert.Equal("persisted_state_changed_during_evaluation", entry.Coverage);
    }

    [Fact]
    public async Task ShutdownAndRestart_DoNotResumeOrRetainASession()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Json(""); });
        fixture.Seed(); fixture.Start("tmdb"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Lifetime.StopApplication(); await fixture.Done();
        using var restartedLifetime = new TestHostApplicationLifetime();
        using var restarted = new MatchingReplayService(fixture.Rig.ReplayReader, fixture.Factory, new(), restartedLifetime);
        Assert.Null(restarted.Snapshot().SessionId); Assert.Equal("idle", restarted.Snapshot().Status); Assert.False(restarted.Snapshot().Running);
        Assert.Equal(1, fixture.Factory.Calls);
    }

    [Theory]
    [InlineData(9, "offline", 30)]
    [InlineData(10, "bad", 30)]
    [InlineData(10, "tmdb", 31)]
    [InlineData(10, "tmdb", 0)]
    public void InvalidLimitsAreRejected(int count, string mode, int budget)
    {
        using var fixture = new Fixture();
        Assert.Throws<ArgumentException>(() => fixture.Replay.Start(new(count, mode, budget)));
        Assert.Equal(0, fixture.Factory.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void ApiAndExport_RequireExistingAuthentication_AndCannotBeCached(bool auth, bool bootstrap)
    {
        using var fixture = new Fixture();
        var context = new DefaultHttpContext(); context.Items[BasicAuthMiddleware.AuthPassedKey] = auth; context.Items[BasicAuthMiddleware.BootstrapTokenActiveKey] = bootstrap;
        var controller = new MatchingReplayController(fixture.Replay) { ControllerContext = new() { HttpContext = context } };
        Assert.Equal(auth && !bootstrap, controller.Get() is OkObjectResult);
        Assert.Equal(auth && !bootstrap, controller.Export() is OkObjectResult);
        Assert.Equal(auth && !bootstrap, controller.Start(new()) is AcceptedResult);
        Assert.Equal(auth && !bootstrap, controller.Stop() is OkObjectResult);
        Assert.Equal(auth && !bootstrap, controller.Reset() is OkObjectResult);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task ExportContainsOnlySanitizedDiagnosticFields()
    {
        using var fixture = new Fixture(); fixture.Seed("A.Movie.2024.https://tracker.example/?apikey=SECRET-CANARY");
        fixture.Start(); await fixture.Done();
        var json = JsonSerializer.Serialize(fixture.Replay.Snapshot());
        Assert.DoesNotContain("SECRET-CANARY", json); Assert.DoesNotContain("tracker.example", json);
        Assert.DoesNotContain("AuthJson", json); Assert.DoesNotContain("SourceId", json); Assert.Contains("titre masqu", json);
    }

    [Fact]
    public async Task SensitiveReleaseContent_IsNeverSentToTmdb()
    {
        using var fixture = new Fixture(); fixture.Seed("Movie.2024.https://tracker.example/?apikey=SECRET-CANARY");
        fixture.Start("tmdb"); var report = await fixture.Done();
        Assert.Equal(0, fixture.Factory.Calls); Assert.Equal(0, report.RequestsUsed);
        Assert.Equal("query_contains_sensitive_content", Assert.Single(report.Entries).Coverage);
        Assert.DoesNotContain("SECRET-CANARY", JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task ReplayApi_InheritsAntiCsrfProtection()
    {
        var context = new DefaultHttpContext(); context.Request.Method = "POST"; context.Request.Path = "/api/diagnostics/matching-shadow/replay";
        context.Request.Scheme = "https"; context.Request.Host = new("feedarr.local"); context.Request.Headers.Origin = "https://untrusted.example";
        var reached = false; var middleware = new AntiCsrfOriginMiddleware(_ => { reached = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(context, new ConfigurationBuilder().Build(), NullLogger<AntiCsrfOriginMiddleware>.Instance);
        Assert.False(reached); Assert.Equal(403, context.Response.StatusCode);
    }
}
