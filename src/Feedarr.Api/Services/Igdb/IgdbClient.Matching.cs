using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Feedarr.Api.Services.Resilience;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Posters;
using System.Security.Cryptography;

namespace Feedarr.Api.Services.Igdb;

public sealed partial class IgdbClient
{
    internal bool MatchingEnabled => GetCreds() is not null;
    internal const int MatchingPageSize = 25;
    internal const int MatchingMaximumPages = 4;
    // Preserve the 100-identity / six-request budget per query. A single 101-row
    // window avoids relevance-order drift between pages; row 101 still refuses
    // truncation. Terminal empty probe + full-window recheck remain mandatory.
    private sealed record CompleteResponse(JsonElement Value);
    internal async Task<JsonElement> ReadMatchingCandidatesAsync(string title, CancellationToken ct)
    {
        var credentials = GetCreds() ?? throw new InvalidDataException("game_provider_unavailable");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            credentials.ClientId + "\0" + credentials.ClientSecret)));
        var fetched = false;
        var response = await _matchingState.Responses.GetAsync($"igdb-window-v1:{fingerprint}:{CleanQuery(title)}",
            async token => { fetched = true; return new CompleteResponse(await ReadCompleteMatchingWindowAsync(title, credentials, token).ConfigureAwait(false)); },
            value => GetCreds() == credentials && ValidMatchingResponse(value.Value), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        MatchingAttemptDiagnostics.Current?.Response("igdb", fetched ? "provider_response" : "cache_or_coalesced");
        return response!.Value;
    }

    private static bool ValidMatchingResponse(JsonElement value)
    {
        try { GameCandidateAdapter.Project("igdb", value, completeIgdbCollection: true); return true; }
        catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException) { return false; }
    }

    private async Task<JsonElement> ReadCompleteMatchingWindowAsync(string title, (string ClientId, string ClientSecret) credentials, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        ct = deadline.Token;
        var token = await GetTokenAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidDataException("game_provider_unavailable");
        var collected = new List<JsonElement>();
        var ids = new HashSet<int>();
        var providerResults = 0;
        var offset = 0;
        const int window = MatchingPageSize * MatchingMaximumPages + 1;
        var requests = 0;
        InvalidDataException Coverage(string reason)
        {
            var error = new InvalidDataException("game_search_coverage_unavailable");
            error.Data["ProviderResultCount"] = providerResults;
            error.Data["IgdbCoverageReason"] = reason;
            error.Data["IgdbOffset"] = offset;
            error.Data["IgdbLimit"] = window;
            error.Data["IgdbUniqueCount"] = ids.Count;
            error.Data["IgdbRequests"] = requests;
            return error;
        }
        static bool Same(JsonElement a, JsonElement b) => JsonNode.DeepEquals(JsonNode.Parse(a.GetRawText()), JsonNode.Parse(b.GetRawText()));
        static int Identity(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var value) || value <= 0)
                throw new InvalidDataException("game_search_identity_invalid");
            return value;
        }
        async Task<JsonElement> Page(int offsetValue, int limit)
        {
            ct.ThrowIfCancellationRequested();
            offset = offsetValue; requests++;
            // Search keeps IGDB's relevance ordering; do not combine search with sort.
            var body = $"search \"{EscapeIgdb(CleanQuery(title))}\"; fields id,name,alternative_names.name,first_release_date,platforms,game_type.type,version_title,version_parent,remakes,remasters,cover.url,summary,genres.name,total_rating,total_rating_count; limit {limit}; offset {offset};";
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games")
            { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
            request.Headers.Add("Client-ID", credentials.ClientId);
            request.Headers.Add("Authorization", $"Bearer {token}");
            request.Options.Set(TransientHttpRetryHandler.DisableRetries, true);
            var watch = Stopwatch.StartNew();
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            _stats.RecordIgdb(response.IsSuccessStatusCode, watch.ElapsedMilliseconds);
            if (!response.IsSuccessStatusCode) throw new InvalidDataException("game_provider_unavailable");
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var value = json.RootElement;
            if (value.ValueKind != JsonValueKind.Array) throw Coverage("invalid_window");
            providerResults = value.GetArrayLength();
            if (providerResults > limit) throw Coverage("invalid_window");
            return value.Clone();
        }
        var first = await Page(0, window).ConfigureAwait(false);
        if (first.GetArrayLength() >= window) throw Coverage("candidate_budget_exceeded");
        foreach (var item in first.EnumerateArray())
        {
            if (!ids.Add(Identity(item))) throw Coverage("duplicate_identity");
            collected.Add(item);
        }
        if (collected.Count > 0)
        {
            if ((await Page(collected.Count, window).ConfigureAwait(false)).GetArrayLength() != 0)
                throw Coverage("terminal_probe_nonempty");
            if (!Same(first, await Page(0, window).ConfigureAwait(false))) throw Coverage("window_changed");
        }
        ct.ThrowIfCancellationRequested();
        MatchingAttemptDiagnostics.Current?.Collection("igdb_single_window", "complete", requests, requests);
        return JsonSerializer.SerializeToElement(collected);
    }
}
