using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Feedarr.Api.Services.Resilience;

namespace Feedarr.Api.Services.Igdb;

public sealed partial class IgdbClient
{
    internal bool MatchingEnabled => GetCreds() is not null;
    internal const int MatchingPageSize = 25;
    internal const int MatchingMaximumPages = 4;
    // Four data pages, one terminal empty probe and (when paged) one head recheck.
    // At most six HTTP searches per query / twelve for the two-query game plan.
    internal async Task<JsonElement> ReadMatchingCandidatesAsync(string title, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        ct = deadline.Token;
        var credentials = GetCreds() ?? throw new InvalidDataException("game_provider_unavailable");
        var token = await GetTokenAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidDataException("game_provider_unavailable");
        var collected = new List<JsonElement>();
        var ids = new HashSet<int>();
        JsonElement? firstPage = null, sentinel = null;
        var providerResults = 0;
        InvalidDataException Coverage()
        {
            var error = new InvalidDataException("game_search_coverage_unavailable");
            error.Data["ProviderResultCount"] = providerResults;
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
        async Task<JsonElement> Page(int offset, int limit)
        {
            ct.ThrowIfCancellationRequested();
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
            if (value.ValueKind != JsonValueKind.Array) throw Coverage();
            providerResults = value.GetArrayLength();
            if (providerResults > limit) throw Coverage();
            return value.Clone();
        }
        for (var page = 0; page < MatchingMaximumPages; page++)
        {
            var response = await Page(page * MatchingPageSize, MatchingPageSize + 1).ConfigureAwait(false);
            firstPage ??= response;
            var entries = response.EnumerateArray().ToArray();
            // Offset stride overlaps the prior sentinel. Any drift, omission or repeat
            // other than this deliberate overlap makes coverage unprovable.
            if (sentinel.HasValue && (entries.Length == 0 || !Same(sentinel.Value, entries[0]))) throw Coverage();
            for (var i = 0; i < Math.Min(entries.Length, MatchingPageSize); i++)
            {
                if (!ids.Add(Identity(entries[i]))) throw Coverage();
                collected.Add(entries[i]);
            }
            if (entries.Length <= MatchingPageSize)
            {
                // A short page is not enough if the server silently capped it.
                if (collected.Count > 0 && (await Page(collected.Count, 1).ConfigureAwait(false)).GetArrayLength() != 0) throw Coverage();
                if (page > 0 && !Same(firstPage.Value, await Page(0, MatchingPageSize + 1).ConfigureAwait(false))) throw Coverage();
                ct.ThrowIfCancellationRequested();
                return JsonSerializer.SerializeToElement(collected);
            }
            sentinel = entries[MatchingPageSize];
            if (ids.Contains(Identity(sentinel.Value))) throw Coverage();
        }
        throw Coverage(); // >100: never pass a truncated set to projection/ranking.
    }
}
