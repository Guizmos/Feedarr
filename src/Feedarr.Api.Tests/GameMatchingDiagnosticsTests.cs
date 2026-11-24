using System.Net;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Posters;
using Microsoft.Extensions.Logging;

namespace Feedarr.Api.Tests;

public sealed class GameMatchingDiagnosticsTests
{
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, System.Text.Encoding.UTF8, "application/json") };
    private const string One = "{\"id\":901,\"name\":\"Game Hit\",\"platforms\":[6]}";

    [Theory]
    [InlineData("empty", 422, "reject", "ranking:no_candidates:no_candidates", 0)]
    [InlineData("rejected", 422, "reject", "ranking:contradiction:candidates_rejected", 1)]
    [InlineData("ambiguous", 422, "ambiguous", "ranking:ambiguity:ambiguous", 2)]
    [InlineData("missing_pc", 422, "ambiguous", "ranking:ambiguity:ambiguous", 1)]
    [InlineData("coverage", 422, "game_search_coverage_unavailable", "collection:coverage_insufficient:game_search_coverage_unavailable", null)]
    [InlineData("invalid_identity", 422, "game_search_identity_invalid", "collection:provider_error:game_search_identity_invalid", null)]
    [InlineData("no_cover", 422, "selected_identity_poster_unavailable", "image_selection:poster_unavailable:selected_identity_poster_unavailable", 1)]
    [InlineData("provider_error", 503, "game_provider_unavailable", "collection:provider_error:game_provider_unavailable", null)]
    [InlineData("invalid_json", 503, "game_provider_or_publication_unavailable", "collection:provider_error:game_provider_or_publication_unavailable", null)]
    [InlineData("empty_image", 503, "game_poster_unavailable", "download:poster_unavailable:game_poster_unavailable", 1)]
    [InlineData("bad_image", 503, "game_provider_or_publication_unavailable", "publication:publication_error:game_provider_or_publication_unavailable", 1)]
    public async Task DiagnosticsPreservePublicDecisionsAndProviderCallCounts(string mode, int status, string reason, string diagnostic, int? count)
    {
        var logger = new CaptureLogger();
        var searches = 0; var images = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit, posterLogger: logger,
            igdbResponse: (request, _) => {
                if (request.RequestUri!.Host == "api.igdb.com")
                {
                    searches++;
                    var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (mode != "coverage" && System.Text.RegularExpressions.Regex.Match(body, @"offset\s+(\d+)") is { Success: true } offset &&
                        int.Parse(offset.Groups[1].Value) > 0) return Task.FromResult<HttpResponseMessage?>(Json("[]"));
                    if (mode == "provider_error") return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.ServiceUnavailable));
                    var payload = mode switch {
                        "empty" => "[]",
                        "rejected" => "[{\"id\":901,\"name\":\"Another Work\",\"platforms\":[6]}]",
                        "ambiguous" => "[" + One + "," + One.Replace("901", "902") + "]",
                        "missing_pc" => "[{\"id\":901,\"name\":\"Game Hit\"}]",
                        "coverage" => "[" + string.Join(",", Enumerable.Range(1, 26).Select(i => One.Replace("901", i.ToString()))) + "]",
                        "invalid_identity" => "[{\"id\":0,\"name\":\"Game Hit\"}]",
                        "invalid_json" => "provider-body-private-marker invalid-json",
                        _ => "[" + One + "]"
                    };
                    if (mode is "empty_image" or "bad_image") return Task.FromResult<HttpResponseMessage?>(null);
                    return Task.FromResult<HttpResponseMessage?>(Json(payload));
                }
                if (request.RequestUri.Host == "images.igdb.com")
                {
                    images++;
                    return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK) { Content = mode == "empty_image"
                        ? new ByteArrayContent(Array.Empty<byte>()) : new StringContent("not-an-image-private-marker") });
                }
                return Task.FromResult<HttpResponseMessage?>(null);
            });
        var id = rig.IngestRelease("Game Hit", UnifiedCategory.JeuWindows);
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var result = await rig.FetchAsync(id);
        Assert.False(result.Ok); Assert.Equal(status, result.StatusCode);
        Assert.Equal(reason, JsonSerializer.SerializeToElement(result.Body).GetProperty("reason").GetString());
        var release = rig.GetReleaseForPoster(id)!;
        Assert.StartsWith("v2:" + diagnostic, release.PosterLastError);
        Assert.Contains("category=JeuWindows;provider=igdb", release.PosterLastError);
        Assert.Contains("candidates=" + (count?.ToString() ?? "unknown"), release.PosterLastError);
        Assert.InRange(release.PosterLastAttemptTs!.Value, before, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.Null(release.ExtProviderId); Assert.Null(release.TmdbId); Assert.Null(release.PosterFile);
        Assert.Equal(mode is "empty" or "provider_error" or "invalid_json" or "invalid_identity" ? 1 : 2, searches);
        Assert.Empty(rig.RawgSearchRequests);
        Assert.Equal(mode is "empty_image" or "bad_image" ? 1 : 0, images);
        var fields = Assert.Single(logger.Diagnostics);
        Assert.Equal(id, fields["ReleaseId"]); Assert.Equal("JeuWindows", fields["Category"]);
        Assert.Equal(count, fields["CandidateCount"]);
        Assert.Equal("igdb", fields["Provider"]); Assert.Equal("igdb", fields["ProvidersQueried"]);
        Assert.NotNull(fields["AttemptedAtTs"]);
        Assert.DoesNotContain("private-marker", release.PosterLastError);
        Assert.DoesNotContain("private-marker", string.Join(" ", fields.Values));
        if (mode == "coverage") { Assert.Equal(26, fields["ProviderResultCount"]); Assert.Contains("providerResults=26", release.PosterLastError); }

        // Persisted failures now participate in the existing cooldown without scheduler changes.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.DoesNotContain(id, await rig.Repository.GetReleaseIdsMissingPosterActionableAsync(10, now));
        Assert.Contains(id, await rig.Repository.GetReleaseIdsMissingPosterActionableAsync(10, now + 901));
    }

    [Fact]
    public async Task RawgPartialCoverageKeepsExistingStatusAndReportsCountWithoutFollowingNextUrl()
    {
        var logger = new CaptureLogger(); var calls = 0; var igdbCalls = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit, posterLogger: logger,
            igdbResponse: (request, _) => {
                if (request.RequestUri!.Host != "api.igdb.com") return Task.FromResult<HttpResponseMessage?>(null);
                igdbCalls++; return Task.FromResult<HttpResponseMessage?>(Json("[]"));
            },
            rawgResponse: (_, _) => { calls++; return Task.FromResult<HttpResponseMessage?>(Json("{\"count\":40,\"next\":\"https://private.example/?apikey=private-marker\",\"results\":[]}")); });
        var id = rig.IngestRelease("Game Hit", UnifiedCategory.JeuWindows);
        var result = await rig.FetchAsync(id);
        Assert.Equal(422, result.StatusCode);
        Assert.Equal("game_search_coverage_unavailable", JsonSerializer.SerializeToElement(result.Body).GetProperty("reason").GetString());
        Assert.Equal(1, calls); Assert.Equal(1, igdbCalls);
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Contains("provider=rawg;queried=igdb+rawg;candidates=0;providerResults=40", row.PosterLastError);
        Assert.DoesNotContain("private-marker", row.PosterLastError);
    }

    [Fact]
    public async Task UnsupportedSecretBearingQueryNeverCallsProvidersOrLogsTheInput()
    {
        var logger = new CaptureLogger();
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit, posterLogger: logger,
            igdbResponse: (_, _) => throw new InvalidOperationException("Provider must not be called"));
        var id = rig.IngestRelease("Game.api_key=private-marker", UnifiedCategory.JeuWindows);
        Assert.Equal(422, (await rig.FetchAsync(id)).StatusCode);
        Assert.Contains("game_query_unsupported", rig.GetReleaseForPoster(id)!.PosterLastError);
        Assert.DoesNotContain("private-marker", rig.GetReleaseForPoster(id)!.PosterLastError);
        Assert.DoesNotContain("private-marker", string.Join(" ", logger.Diagnostics.SelectMany(d => d.Values)));
        Assert.Empty(rig.IgdbSearchRequests); Assert.Empty(rig.RawgSearchRequests);
    }

    [Fact]
    public async Task DiagnosticCannotOverwriteAManualAssociationCreatedDuringARejectedSearch()
    {
        PosterMatchingContractTestRig? current = null; long id = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbMiss,
            igdbResponse: (request, _) => {
                if (request.RequestUri!.Host != "api.igdb.com") return Task.FromResult<HttpResponseMessage?>(null);
                current!.Repository.SetManualMatch(id, "igdb", "game", "902");
                return Task.FromResult<HttpResponseMessage?>(Json("[]"));
            });
        current = rig; id = rig.IngestRelease("Game Hit", UnifiedCategory.JeuWindows);
        Assert.Equal(422, (await rig.FetchAsync(id)).StatusCode); // Existing decision remains unchanged.
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Equal("igdb", row.ManualMatchProvider); Assert.Equal("902", row.ManualMatchId);
        Assert.Null(row.PosterLastError); Assert.Null(row.PosterLastAttemptTs);
    }

    [Fact]
    public async Task PublicationScopeContradictionKeepsIts422AndLeavesBothIdentitiesEmpty()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit);
        var id = rig.IngestRelease("Game Hit", UnifiedCategory.JeuWindows);
        var other = rig.IngestRelease("Game Hit Deluxe Edition", UnifiedCategory.JeuWindows);
        using (var db = rig.Database.Open()) db.Execute("UPDATE releases SET entity_id=(SELECT entity_id FROM releases WHERE id=@id) WHERE id=@other", new { id, other });
        Assert.Equal(422, (await rig.FetchAsync(id)).StatusCode);
        Assert.Contains("v2:publication:publication_error:game_shared_entity_scope_unverified", rig.GetReleaseForPoster(id)!.PosterLastError);
        Assert.Null(rig.GetReleaseForPoster(id)!.ExtProviderId); Assert.Null(rig.GetReleaseForPoster(other)!.ExtProviderId);
    }

    [Fact]
    public async Task NoConfiguredProviderKeepsIts503WithoutAnyHttpCall()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit,
            igdbResponse: (_, _) => throw new InvalidOperationException("No HTTP call allowed"));
        using (var db = rig.Database.Open()) db.Execute("UPDATE external_provider_instances SET enabled=0 WHERE provider_key IN ('igdb','rawg')");
        var id = rig.IngestRelease("Game Hit", UnifiedCategory.JeuWindows);
        var result = await rig.FetchAsync(id);
        Assert.Equal(503, result.StatusCode);
        Assert.Equal("game_provider_not_configured", JsonSerializer.SerializeToElement(result.Body).GetProperty("reason").GetString());
        Assert.Contains("provider_error:game_provider_not_configured", rig.GetReleaseForPoster(id)!.PosterLastError);
        Assert.Empty(rig.IgdbSearchRequests); Assert.Empty(rig.RawgSearchRequests);
    }

    [Fact]
    public async Task ProviderErrorFollowedBySuccessfulFallbackAddsNoCallsAndLeavesNoFailure()
    {
        var logger = new CaptureLogger(); var igdbCalls = 0; var rawgImages = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit, posterLogger: logger,
            igdbResponse: (request, _) => {
                if (request.RequestUri!.Host != "api.igdb.com") return Task.FromResult<HttpResponseMessage?>(null);
                igdbCalls++; return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.ServiceUnavailable));
            }, rawgResponse: (request, _) => {
                if (request.RequestUri!.Host == "rawg.test") rawgImages++;
                return Task.FromResult<HttpResponseMessage?>(null);
            });
        var id = rig.IngestRelease("Game Hit", UnifiedCategory.JeuWindows);
        Assert.True((await rig.FetchAsync(id)).Ok);
        Assert.Equal(1, igdbCalls); Assert.Single(rig.RawgSearchRequests); Assert.Equal(1, rawgImages);
        Assert.Null(rig.GetReleaseForPoster(id)!.PosterLastError);
        Assert.Equal("rawg", rig.GetReleaseForPoster(id)!.ExtProvider);
        Assert.Equal("game_provider_unavailable", Assert.Single(logger.Diagnostics)["Decision"]);
    }

    private sealed class CaptureLogger : ILogger<PosterFetchService>
    {
        internal List<Dictionary<string, object?>> Diagnostics { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values) return;
            var fields = values.ToDictionary(p => p.Key, p => p.Value);
            if (fields.ContainsKey("Decision")) Diagnostics.Add(fields);
        }
    }
}
