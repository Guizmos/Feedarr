using System.Net;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Controllers;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Matching.Replay;
using Feedarr.Api.Services.Resilience;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Feedarr.Api.Tests;

public sealed partial class MatchingReplayTests
{
    private static ReplaySelection Selection(string method = "sample", string[]? categories = null, long[]? ids = null, string filter = "all", bool fair = false)
        => new(method, categories, ids, filter, fair);
    private static ReplayRequest Request(Fixture fixture, ReplaySelection? selection = null, string mode = "offline", int budget = 30)
        => new(10, mode, budget, fixture.Replay.Snapshot().Generation, selection);

    [Theory]
    [InlineData("films", UnifiedCategory.Film)]
    [InlineData("series", UnifiedCategory.Serie)]
    [InlineData("animation", UnifiedCategory.Animation)]
    [InlineData("emissions", UnifiedCategory.Emission)]
    [InlineData("spectacle", UnifiedCategory.Spectacle)]
    public void PreviewFiltersCategories_WithoutNetworkOrWrites(string category, UnifiedCategory enumCategory)
    {
        using var fixture = new Fixture();
        var wanted = fixture.Seed(category: enumCategory);
        fixture.Seed("Game.v1.2-CODEX", UnifiedCategory.JeuWindows);
        var before = fixture.DiskSnapshot();
        var preview = fixture.Replay.Preview(Request(fixture, Selection(categories: new[] { category }), "tmdb"));
        Assert.Equal(new[] { wanted }, preview.SelectedIds);
        Assert.Equal(2, preview.MinimumRequests); Assert.True(preview.PaginationUnknown);
        Assert.Equal(category, Assert.Single(preview.Distribution).Category);
        Assert.Equal(0, fixture.Factory.Created); Assert.Equal(before, fixture.DiskSnapshot());
        Assert.Equal("idle", fixture.Replay.Snapshot().Status);
    }

    [Fact]
    public void PreviewFiltersPosterAndIdentityMetadata()
    {
        using var fixture = new Fixture();
        var free = fixture.Seed("Free.Movie.2024.BluRay"); var poster = fixture.Seed("Poster.Movie.2024.BluRay"); var identity = fixture.Seed("Identified.Movie.2024.BluRay");
        using (var connection = fixture.Rig.Database.Open())
        {
            connection.Execute("UPDATE releases SET poster_file='existing.png' WHERE id=@poster", new { poster });
            connection.Execute("UPDATE media_entities SET tmdb_id=77,tmdb_media_type='movie' WHERE id=(SELECT entity_id FROM releases WHERE id=@identity)", new { identity });
        }
        long[] Ids(string filter) => fixture.Replay.Preview(Request(fixture, Selection(filter: filter))).SelectedIds.Order().ToArray();
        Assert.Equal(new[] { free, identity }.Order(), Ids("without_poster"));
        Assert.Equal(new[] { free, poster }.Order(), Ids("without_identity"));
        Assert.Equal(new[] { identity }, Ids("with_identity"));
        Assert.Equal(3, Ids("all").Length);
        Assert.Throws<ArgumentException>(() => fixture.Replay.Preview(Request(fixture, Selection("ids", ids: new[] { free }, filter: "with_identity"))));
    }

    [Fact]
    public async Task ExactIdsRemainOrderedAndReplayable_WhenCatalogueChanges()
    {
        using var fixture = new Fixture(); var first = fixture.Seed(); var second = fixture.Seed();
        var request = Request(fixture, Selection("ids", ids: new[] { second, first }));
        var preview = fixture.Replay.Preview(request);
        fixture.Replay.Start(request with { ExpectedIds = preview.SelectedIds }); var report = await fixture.Done();
        Assert.Equal(new[] { second, first }, report.SelectedIds); Assert.Equal(report.SelectedIds, report.ProcessedIds);
        Assert.Equal(2, report.Requested); Assert.Equal("ids", report.Selection!.Method);
        fixture.Seed("New.Movie.2024.BluRay");
        fixture.Replay.Start(Request(fixture, report.Selection)); var repeated = await fixture.Done();
        Assert.Equal(report.SelectedIds, repeated.SelectedIds); Assert.Equal(report.ProcessedIds, repeated.ProcessedIds);
        Assert.Equal(0, fixture.Factory.Created);
        var json = JsonSerializer.Serialize(repeated);
        Assert.Contains("SelectedIds", json); Assert.Contains("ProcessedIds", json); Assert.Contains("ReleaseIds", json);
        Assert.DoesNotContain("AuthJson", json); Assert.DoesNotContain("SourceId", json);
    }

    [Fact]
    public void PreviewedSelectionCannotBeSilentlyReplacedAtStart()
    {
        using var fixture = new Fixture(); fixture.Seed();
        var request = Request(fixture, mode: "tmdb"); var preview = fixture.Replay.Preview(request);
        fixture.Seed();
        Assert.Throws<InvalidOperationException>(() => fixture.Replay.Start(request with { ExpectedIds = preview.SelectedIds }));
        Assert.Equal(0, fixture.Factory.Created); Assert.False(fixture.Replay.Snapshot().Running);
    }

    [Theory]
    [InlineData("method")]
    [InlineData("filter")]
    [InlineData("category")]
    [InlineData("no-categories")]
    [InlineData("duplicate-category")]
    [InlineData("empty-ids")]
    [InlineData("duplicate-id")]
    [InlineData("negative-id")]
    [InlineData("huge-id")]
    [InlineData("too-many-ids")]
    [InlineData("unexpected-ids")]
    [InlineData("missing-id")]
    public void InvalidSelectionsNeverCreateHttpClient(string fault)
    {
        using var fixture = new Fixture(); var id = fixture.Seed();
        var selection = fault switch
        {
            "method" => Selection("raw-sql"), "filter" => Selection(filter: "bad"),
            "category" => Selection(categories: new[] { "games" }), "no-categories" => Selection(categories: Array.Empty<string>()),
            "duplicate-category" => Selection(categories: new[] { "films", "films" }), "empty-ids" => Selection("ids"),
            "duplicate-id" => Selection("ids", ids: new[] { id, id }), "negative-id" => Selection("ids", ids: new[] { -1L }),
            "huge-id" => Selection("ids", ids: new[] { long.MaxValue }), "too-many-ids" => Selection("ids", ids: Enumerable.Range(1, 201).Select(i => (long)i).ToArray()),
            "unexpected-ids" => Selection(ids: new[] { id }), _ => Selection("ids", ids: new[] { 999999L })
        };
        var request = Request(fixture, selection, "tmdb");
        Assert.Throws<ArgumentException>(() => fixture.Replay.Preview(request));
        Assert.Throws<ArgumentException>(() => fixture.Replay.Start(request));
        Assert.Equal(0, fixture.Factory.Created);
    }

    [Fact]
    public async Task SelectionArraysAreClonedAndCannotBeChangedByCaller()
    {
        using var fixture = new Fixture(); var id = fixture.Seed(); var ids = new[] { id }; var categories = new[] { "films" };
        fixture.Replay.Start(Request(fixture, Selection("ids", categories, ids)));
        ids[0] = 99999; categories[0] = "games";
        var report = await fixture.Done(); Assert.Equal(new[] { id }, report.SelectedIds); Assert.Equal(new[] { "films" }, report.Selection!.Categories);
    }

    [Fact]
    public void ShadowDivergencesSelectionUsesOnlyTheCurrentBoundedWindow()
    {
        using var fixture = new Fixture(); var id = fixture.Seed(); fixture.Seed();
        var state = fixture.Rig.ReplayReader.Read(id)!;
        var candidate = new Feedarr.Api.Services.Tmdb.TmdbClient.SearchResult(1184918, "Le Robot sauvage", "The Wild Robot", null, "movie", 2024, "en");
        var batch = new MatchingShadowBatch("Le Robot sauvage", 2024, new[] { candidate }, true);
        var observation = MatchingShadowEvaluator.Observe(true, state, () => state, batch, new("Reject", null, null))!;
        fixture.Shadow.SetEnabled(true); fixture.Shadow.Publish(fixture.Shadow.TryBegin()!.Value, observation, state.RawTitle, batch, 1);
        Assert.True(Assert.Single(fixture.Shadow.Snapshot().Entries).Divergence);
        var request = Request(fixture, Selection("shadow_divergences"));
        var preview = fixture.Replay.Preview(request); Assert.Equal(new[] { id }, preview.SelectedIds);
        fixture.Shadow.Reset();
        Assert.Empty(fixture.Replay.Preview(request).SelectedIds);
        Assert.Throws<InvalidOperationException>(() => fixture.Replay.Start(request with { ExpectedIds = preview.SelectedIds }));
        Assert.Equal(0, fixture.Factory.Created);
    }

    [Fact]
    public async Task CategoryQuotasAreFairAndCannotBorrowAnotherCategoryBudget()
    {
        using var fixture = new Fixture();
        fixture.Seed(); fixture.Seed(); fixture.Seed(category: UnifiedCategory.Serie); fixture.Seed(category: UnifiedCategory.Serie); fixture.Seed(category: UnifiedCategory.Spectacle);
        var request = Request(fixture, Selection(categories: new[] { "films", "series", "spectacle" }, fair: true), "tmdb", 6);
        var preview = fixture.Replay.Preview(request);
        Assert.Equal(10, preview.MinimumRequests); Assert.True(preview.MinimumExceedsBudget);
        Assert.All(preview.CategoryBudgets, q => Assert.Equal(2, q.Limit));
        fixture.Replay.Start(request); var report = await fixture.Done();
        Assert.Equal(6, report.RequestsUsed); Assert.Equal(6, fixture.Factory.Calls);
        Assert.All(report.CategoryBudgets!, q => { Assert.Equal(2, q.Limit); Assert.Equal(2, q.Used); });
        Assert.Contains(report.Entries, e => e.Coverage == "category_http_budget_insufficient");
        Assert.Equal(3, report.Entries.Count(e => e.ProviderEvidenceComplete));
    }

    [Fact]
    public async Task TooSmallFairQuotasDoNotWasteCallsOnIncompleteRelease()
    {
        using var fixture = new Fixture(); fixture.Seed(); fixture.Seed(category: UnifiedCategory.Serie); fixture.Seed(category: UnifiedCategory.Spectacle);
        fixture.Replay.Start(Request(fixture, Selection(fair: true), "tmdb", 3)); var report = await fixture.Done();
        Assert.Equal(0, fixture.Factory.Calls); Assert.Equal(0, report.RequestsUsed);
        Assert.All(report.Entries, e => Assert.Equal("category_http_budget_insufficient", e.Coverage));
        Assert.Equal(3, report.CategoryBudgets!.Sum(q => q.Limit));
    }

    [Fact]
    public async Task PaginationConsumesOnlyItsCategoryQuota_AndOtherCategoryCanStillRun()
    {
        using var fixture = new Fixture((request, _) =>
        {
            var query = QueryHelpers.ParseQuery(request.RequestUri!.Query); var page = int.Parse(query["page"]!);
            var isSeries = request.RequestUri.AbsolutePath.EndsWith("tv");
            return Task.FromResult(Json(JsonSerializer.Serialize(new { page, total_pages = isSeries ? 1 : 2, total_results = isSeries ? 1 : 2,
                results = new[] { new { id = page, title = "Le Robot sauvage", original_title = "The Wild Robot", release_date = "2024-01-01", first_air_date = "2024-01-01" } } })));
        });
        fixture.Seed(); fixture.Seed(category: UnifiedCategory.Serie);
        fixture.Replay.Start(Request(fixture, Selection(fair: true), "tmdb", 4)); var report = await fixture.Done();
        Assert.Equal(4, report.RequestsUsed);
        Assert.Contains(report.Entries, e => e.Category == "films" && e.Coverage == "category_http_budget_exhausted" && !e.ProviderEvidenceComplete);
        Assert.Contains(report.Entries, e => e.Category == "series" && e.Detail.Decision == "accept" && e.ProviderEvidenceComplete);
        Assert.All(report.CategoryBudgets!, q => Assert.Equal(2, q.Used));
    }

    [Fact]
    public async Task ChildBudgetCountsRedirectsAndErrorsInsideParentCap()
    {
        var calls = 0;
        using var http = new HttpClient(new ProtocolDowngradeRedirectHandler { InnerHandler = new ReplayBudgetHandler { InnerHandler = new Handler((_, _) =>
        {
            calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://api.themoviedb.org/3/search/movie?page=2") } });
        }) } });
        var parent = new ReplayHttpBudget(3, TimeSpan.Zero); var category = new ReplayHttpBudget(2, parent: parent);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.themoviedb.org/3/search/movie"); request.Options.Set(ReplayHttpBudget.Option, category);
        await Assert.ThrowsAsync<ReplayCategoryBudgetExceededException>(() => http.SendAsync(request));
        Assert.Equal(2, calls); Assert.Equal(2, category.Used); Assert.Equal(2, parent.Used);
    }

    [Fact]
    public async Task CategoryBudgetCountsFailedTransportAttempts()
    {
        var calls = 0;
        using var http = new HttpClient(new ReplayBudgetHandler { InnerHandler = new Handler((_, _) => { calls++; throw new HttpRequestException("simulated"); }) });
        var parent = new ReplayHttpBudget(3, TimeSpan.Zero); var category = new ReplayHttpBudget(2, parent: parent);
        for (var i = 0; i < 2; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.themoviedb.org/3/search/movie"); request.Options.Set(ReplayHttpBudget.Option, category);
            await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(request));
        }
        using var denied = new HttpRequestMessage(HttpMethod.Get, "https://api.themoviedb.org/3/search/movie"); denied.Options.Set(ReplayHttpBudget.Option, category);
        await Assert.ThrowsAsync<ReplayCategoryBudgetExceededException>(() => http.SendAsync(denied));
        Assert.Equal(2, parent.Used); Assert.Equal(2, category.Used); Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(9007199254740992L)]
    public void InvalidPreviewIdsAreRejectedBeforeAnyRequest(long id)
    {
        using var fixture = new Fixture();
        Assert.Throws<ArgumentException>(() => fixture.Replay.Start(Request(fixture) with { ExpectedIds = new[] { id } }));
        Assert.Equal(0, fixture.Factory.Created);
    }

    [Fact]
    public async Task OfflineFairReplayAndPreviewNeverUseNetwork()
    {
        using var fixture = new Fixture(); fixture.Seed(); fixture.Seed(category: UnifiedCategory.Serie);
        var request = Request(fixture, Selection(fair: true), budget: 1); var before = fixture.DiskSnapshot();
        Assert.Equal(0, fixture.Replay.Preview(request).MinimumRequests);
        fixture.Replay.Start(request); var report = await fixture.Done();
        Assert.Equal(0, report.RequestsUsed); Assert.Equal(0, fixture.Factory.Created); Assert.Equal(before, fixture.DiskSnapshot());
    }

    [Fact]
    public async Task EvidenceFingerprintDistinguishesProviderChangesFromQueryChanges()
    {
        var year = 2024;
        using var fixture = new Fixture((request, _) =>
        {
            var french = QueryHelpers.ParseQuery(request.RequestUri!.Query).ContainsKey("language");
            return Task.FromResult(Json(JsonSerializer.Serialize(new { page = 1, total_pages = 1, total_results = 1,
                results = new[] { new { id = 1184918, title = french ? "Le Robot sauvage" : "The Wild Robot", original_title = "The Wild Robot", release_date = $"{year}-01-01", original_language = "en" } } })));
        });
        var id = fixture.Seed(); var selection = Selection("ids", ids: new[] { id });
        fixture.Replay.Start(Request(fixture, selection, "tmdb")); var first = Assert.Single((await fixture.Done()).Entries);
        year = 2023;
        fixture.Replay.Start(Request(fixture, selection, "tmdb")); var second = Assert.Single((await fixture.Done()).Entries);
        Assert.True(first.ProviderEvidenceComplete); Assert.True(second.ProviderEvidenceComplete);
        Assert.NotEqual(first.ProviderEvidenceFingerprint, second.ProviderEvidenceFingerprint);
        Assert.Equal(first.QueryPlanFingerprint, second.QueryPlanFingerprint); Assert.Equal("fr-FR", first.SearchLocale);
        Assert.Equal(64, first.ProviderEvidenceFingerprint!.Length);
    }

    [Fact]
    public async Task PartialEvidenceAndCompleteEmptyResponsesAreNotConfused()
    {
        using var partial = new Fixture(); partial.Seed(); partial.Start("tmdb", budget: 1);
        var entry = Assert.Single((await partial.Done()).Entries);
        Assert.NotNull(entry.ProviderEvidenceFingerprint); Assert.False(entry.ProviderEvidenceComplete); Assert.NotNull(entry.QueryPlanFingerprint);
        using var empty = new Fixture((_, _) => Task.FromResult(Json("{\"page\":1,\"total_pages\":0,\"total_results\":0,\"results\":[]}")));
        empty.Seed(); empty.Start("tmdb"); var emptyEntry = Assert.Single((await empty.Done()).Entries);
        Assert.True(emptyEntry.ProviderEvidenceComplete); Assert.NotNull(emptyEntry.ProviderEvidenceFingerprint); Assert.Equal("reject", emptyEntry.Detail.Decision);
    }

    [Fact]
    public async Task StopAndResetKeepSelectedIdsButNeverPublishLateProcessedIds()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (request, _) => { entered.TrySetResult(); await release.Task; return Film(request); });
        var id = fixture.Seed(); var request = Request(fixture, Selection("ids", ids: new[] { id }, fair: true), "tmdb", 2);
        fixture.Replay.Start(request); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = fixture.Replay.Stop(); Assert.Equal(new[] { id }, stopped.SelectedIds); Assert.Empty(stopped.ProcessedIds!);
        Assert.Throws<InvalidOperationException>(() => fixture.Replay.Start(request with { Generation = stopped.Generation }));
        release.TrySetResult(); var report = await fixture.Done(); Assert.Empty(report.ProcessedIds!); Assert.Empty(report.Entries);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void PreviewRequiresExistingAuthentication(bool authenticated, bool bootstrap)
    {
        using var fixture = new Fixture(); fixture.Seed();
        var context = new DefaultHttpContext(); context.Items[BasicAuthMiddleware.AuthPassedKey] = authenticated; context.Items[BasicAuthMiddleware.BootstrapTokenActiveKey] = bootstrap;
        var controller = new MatchingReplayController(fixture.Replay) { ControllerContext = new() { HttpContext = context } };
        Assert.Equal(authenticated && !bootstrap, controller.Preview(new()) is OkObjectResult);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString()); Assert.Equal(0, fixture.Factory.Created);
    }
}
