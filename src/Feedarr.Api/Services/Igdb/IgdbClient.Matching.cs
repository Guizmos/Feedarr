using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Feedarr.Api.Services.Resilience;

namespace Feedarr.Api.Services.Igdb;

public sealed partial class IgdbClient
{
    internal bool MatchingEnabled => GetCreds() is not null;

    internal async Task<JsonElement> ReadMatchingCandidatesAsync(string title, CancellationToken ct)
    {
        var credentials = GetCreds() ?? throw new InvalidDataException("game_provider_unavailable");
        var token = await GetTokenAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidDataException("game_provider_unavailable");
        // One sentinel beyond the budget: never silently hide competing editions, even without artwork.
        var body = $"search \"{EscapeIgdb(CleanQuery(title))}\"; fields id,name,alternative_names.name,first_release_date,platforms,game_type.type,version_title,version_parent,remakes,remasters,cover.url,summary,genres.name,total_rating,total_rating_count; limit 26;";
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
        return json.RootElement.Clone();
    }
}
