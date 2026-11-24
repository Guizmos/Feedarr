using System.Net;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Posters;
using Microsoft.Extensions.Logging;

namespace Feedarr.Api.Tests;

public sealed class RawgMatchingDiagnosticsTests
{
    private const string Private = "private-provider-marker";
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    private static Task<HttpResponseMessage?> IgdbEmpty(HttpRequestMessage request, CancellationToken _)
        => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.Host == "api.igdb.com" ? Json("[]") : null);
    private static JsonElement Section(string value, string name)
    {
        var start = value.IndexOf(";" + name + "=", StringComparison.Ordinal) + name.Length + 2;
        Assert.True(start >= name.Length + 2);
        var end = value.IndexOf(';', start);
        return JsonDocument.Parse(end < 0 ? value[start..] : value[start..end]).RootElement.Clone();
    }

    [Theory]
    [InlineData("401", "rawg_http_unauthorized", 401, "game_provider_unavailable", 503)]
    [InlineData("403", "rawg_http_forbidden", 403, "game_provider_unavailable", 503)]
    [InlineData("429", "rawg_http_rate_limited", 429, "game_provider_unavailable", 503)]
    [InlineData("500", "rawg_http_server_error", 500, "game_provider_unavailable", 503)]
    [InlineData("503", "rawg_http_server_error", 503, "game_provider_unavailable", 503)]
    [InlineData("400", "rawg_http_client_error", 400, "game_provider_unavailable", 503)]
    [InlineData("timeout", "rawg_timeout", null, "game_provider_or_publication_unavailable", 503)]
    [InlineData("network", "rawg_network_error", null, "game_provider_or_publication_unavailable", 503)]
    [InlineData("body_io", "rawg_network_error", null, "game_provider_or_publication_unavailable", 503)]
    [InlineData("json", "rawg_invalid_response", 200, "game_provider_or_publication_unavailable", 503)]
    [InlineData("shape", "rawg_invalid_response", 200, "game_provider_or_publication_unavailable", 503)]
    [InlineData("fields", "rawg_invalid_response", 200, "game_search_coverage_unavailable", 422)]
    [InlineData("count_type", "rawg_invalid_response", 200, "game_search_coverage_unavailable", 422)]
    [InlineData("partial", "rawg_coverage_unavailable", 200, "game_search_coverage_unavailable", 422)]
    public async Task TechnicalFailuresPreserveEmptyIgdbDecisionAndPublicOutcome(string mode, string technical, int? http, string publicReason, int status)
    {
        var logger = new CaptureLogger(); var calls = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit,
            posterLogger: logger, igdbResponse: IgdbEmpty, rawgResponse: (_, _) => {
                calls++;
                if (mode == "timeout") throw new TaskCanceledException(Private + "?key=secret");
                if (mode == "network") throw new HttpRequestException(Private + "?key=secret");
                if (mode == "body_io") throw new IOException(Private + "?key=secret");
                return Task.FromResult<HttpResponseMessage?>(mode switch {
                    "json" => Json(Private + " invalid-json"), "shape" => Json("[]"),
                    "fields" => Json("{\"detail\":\"" + Private + "\"}"),
                    "count_type" => Json("{\"count\":\"" + Private + "\",\"next\":null,\"results\":[]}"),
                    "partial" => Json("{\"count\":40,\"next\":\"https://example.test/?key=" + Private + "\",\"results\":[]}"),
                    _ => new((HttpStatusCode)int.Parse(mode)) { Content = new StringContent(Private) }
                });
            });
        var id = rig.IngestRelease("Game Hit", UnifiedCategory.JeuWindows);
        var result = await rig.FetchAsync(id);
        Assert.False(result.Ok); Assert.Equal(status, result.StatusCode);
        Assert.Equal(publicReason, JsonSerializer.SerializeToElement(result.Body).GetProperty("reason").GetString());
        Assert.Equal(1, calls);
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Null(row.ExtProviderId); Assert.Null(row.PosterFile); Assert.NotNull(row.PosterLastAttemptTs);
        var igdb = Section(row.PosterLastError!, "igdb"); var rawg = Section(row.PosterLastError!, "rawg");
        Assert.Equal("ranking", igdb.GetProperty("Stage").GetString());
        Assert.Equal("reject", igdb.GetProperty("Decision").GetString());
        Assert.Equal("no-provider-results", igdb.GetProperty("Reasons")[0].GetString());
        Assert.Equal(0, igdb.GetProperty("CandidateCount").GetInt32());
        Assert.Equal(technical, rawg.GetProperty("Reason").GetString());
        Assert.Equal(http, rawg.GetProperty("HttpStatus").Deserialize<int?>());
        Assert.Equal(technical, Assert.Single(logger.Entries)["RawgFailureReason"]);
        var recorded = row.PosterLastError + JsonSerializer.Serialize(logger.Entries);
        Assert.DoesNotContain(Private, recorded); Assert.DoesNotContain("rawg-test-key", recorded);
        Assert.DoesNotContain("?key=", recorded); Assert.DoesNotContain("https://", recorded);
    }

    [Theory]
    [InlineData("rejected", "reject")]
    [InlineData("ambiguous", "ambiguous")]
    public async Task ScoresAndContradictionsSurviveRawgFailure(string mode, string decision)
    {
        var calls = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit,
            igdbResponse: (request, _) => {
                if (request.RequestUri!.Host != "api.igdb.com") return Task.FromResult<HttpResponseMessage?>(null);
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var data = body.Contains("offset 0;") ? mode == "rejected"
                    ? "[{\"id\":901,\"name\":\"Game Hit\",\"platforms\":[48]}]"
                    : "[{\"id\":901,\"name\":\"Game Hit\",\"platforms\":[6]},{\"id\":902,\"name\":\"Game Hit\",\"platforms\":[6]}]" : "[]";
                return Task.FromResult<HttpResponseMessage?>(Json(data));
            }, rawgResponse: (_, _) => { calls++; return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.Unauthorized)); });
        var id = rig.IngestRelease("Game Hit", UnifiedCategory.JeuWindows);
        Assert.Equal(503, (await rig.FetchAsync(id)).StatusCode); Assert.Equal(1, calls);
        var igdb = Section(rig.GetReleaseForPoster(id)!.PosterLastError!, "igdb");
        Assert.Equal(decision, igdb.GetProperty("Decision").GetString());
        var candidates = igdb.GetProperty("Candidates");
        Assert.Equal(mode == "rejected" ? 1 : 2, candidates.GetArrayLength());
        Assert.All(candidates.EnumerateArray(), c => {
            Assert.True(c.GetProperty("Confidence").GetDouble() >= 0);
            Assert.NotEmpty(c.GetProperty("Contradictions").EnumerateArray());
            Assert.Equal("igdb", c.GetProperty("Identity").GetProperty("Provider").GetString());
        });
        if (mode == "rejected") Assert.Contains(candidates[0].GetProperty("Contradictions").EnumerateArray(), c => c.GetProperty("Code").GetString() == "pc-platform-mismatch");
    }

    [Fact]
    public async Task IgdbCollectionFailureIsNotMistakenForAnEmptySuccessfulSearch()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit,
            igdbResponse: (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.Host == "api.igdb.com"
                ? new(HttpStatusCode.ServiceUnavailable) : null),
            rawgResponse: (_, _) => Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.Forbidden)));
        var id = rig.IngestRelease("Game Hit", UnifiedCategory.JeuWindows);
        Assert.Equal(503, (await rig.FetchAsync(id)).StatusCode);
        var persisted = rig.GetReleaseForPoster(id)!.PosterLastError!;
        var failure = Section(persisted, "igdb").GetProperty("CollectionFailure");
        Assert.Equal("collection", failure.GetProperty("Stage").GetString());
        Assert.Equal("game_provider_unavailable", failure.GetProperty("Reason").GetString());
        Assert.Equal(403, Section(persisted, "rawg").GetProperty("HttpStatus").GetInt32());
    }

    [Fact]
    public async Task RawgFailureCannotOverwriteAConcurrentManualIdentity()
    {
        PosterMatchingContractTestRig? current = null; long id = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit,
            igdbResponse: IgdbEmpty, rawgResponse: (_, _) => {
                current!.Repository.SetManualMatch(id, "igdb", "game", "902");
                return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.Unauthorized));
            });
        current = rig; id = rig.IngestRelease("Game Hit", UnifiedCategory.JeuWindows);
        Assert.Equal(503, (await rig.FetchAsync(id)).StatusCode);
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Equal("902", row.ManualMatchId);
        Assert.Null(row.PosterLastError); Assert.Null(row.PosterLastAttemptTs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrDisabledKeyIsClassifiedWithoutHttpOrFallbackChange(bool disabled)
    {
        var calls = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit,
            igdbResponse: IgdbEmpty, rawgResponse: (_, _) => { calls++; throw new InvalidOperationException("Must not send"); });
        using (var conn = rig.Database.Open())
            conn.Execute(disabled ? "UPDATE external_provider_instances SET enabled=0 WHERE provider_key='rawg'"
                : "UPDATE external_provider_instances SET auth_json='{}' WHERE provider_key='rawg'");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => rig.Rawg.ReadMatchingCandidatesAsync("Game Hit", default));
        Assert.Equal("game_provider_unavailable", error.Message);
        Assert.Equal("rawg_configuration_unavailable", error.Data["RawgFailureReason"]);
        Assert.False(error.Data.Contains("RawgHttpStatus"));
        var id = rig.IngestRelease("Game Hit", UnifiedCategory.JeuWindows);
        Assert.Equal(422, (await rig.FetchAsync(id)).StatusCode); // No configured RAWG, no fallback, as before.
        Assert.Equal(0, calls);
        var persisted = rig.GetReleaseForPoster(id)!.PosterLastError!;
        Assert.Equal("rawg_configuration_unavailable", Section(persisted, "rawg").GetProperty("Reason").GetString());
        Assert.Equal("configuration", Section(persisted, "rawg").GetProperty("Stage").GetString());
        Assert.Equal("reject", Section(persisted, "igdb").GetProperty("Decision").GetString());
    }

    [Fact]
    public async Task InvalidConfigurationIsUnavailableWithoutHttpOrFallbackChange()
    {
        var calls = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit,
            igdbResponse: IgdbEmpty, rawgResponse: (_, _) => { calls++; throw new InvalidOperationException("Must not send"); });
        using (var conn = rig.Database.Open())
            conn.Execute("UPDATE external_provider_instances SET auth_json='[\"invalid-private-config\"]' WHERE provider_key='rawg'");
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => rig.Rawg.ReadMatchingCandidatesAsync("Game Hit", default));
        Assert.Equal("game_provider_unavailable", error.Message);
        Assert.Equal("rawg_configuration_unavailable", error.Data["RawgFailureReason"]);
        Assert.False(rig.Rawg.IsConfigured());
        var id = rig.IngestRelease("Game Hit", UnifiedCategory.JeuWindows);
        Assert.Equal(422, (await rig.FetchAsync(id)).StatusCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AcceptedIgdbNeverCallsRawg()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit,
            igdbResponse: (request, _) => {
                if (request.RequestUri!.Host != "api.igdb.com") return Task.FromResult<HttpResponseMessage?>(null);
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Task.FromResult<HttpResponseMessage?>(Json(body.Contains("offset 0;")
                    ? "[{\"id\":901,\"name\":\"Game Hit\",\"platforms\":[6],\"cover\":{\"url\":\"https://images.igdb.com/igdb/image/upload/t_cover_big/x.jpg\"}}]" : "[]"));
            }, rawgResponse: (_, _) => throw new InvalidOperationException("RAWG must not be called"));
        var id = rig.IngestRelease("Game Hit", UnifiedCategory.JeuWindows);
        Assert.True((await rig.FetchAsync(id)).Ok);
        Assert.Equal("igdb", rig.GetReleaseForPoster(id)!.ExtProvider); Assert.Null(rig.GetReleaseForPoster(id)!.PosterLastError);
    }

    [Fact]
    public async Task CallerCancellationIsStillPropagated()
    {
        using var cancellation = new CancellationTokenSource();
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit,
            igdbResponse: IgdbEmpty, rawgResponse: (_, ct) => { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult<HttpResponseMessage?>(null); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Rawg.ReadMatchingCandidatesAsync("Game Hit", cancellation.Token));
    }

    private sealed class CaptureLogger : ILogger<PosterFetchService>
    {
        internal List<Dictionary<string, object?>> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values) {
                var fields = values.ToDictionary(x => x.Key, x => x.Value);
                if (fields.ContainsKey("Decision")) Entries.Add(fields);
            }
        }
    }
}
