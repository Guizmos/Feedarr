using System.Diagnostics;
using System.Text.Json;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Resilience;

namespace Feedarr.Api.Services.Rawg;

public sealed partial class RawgClient
{
    internal async Task<JsonElement> ReadMatchingCandidatesAsync(string title, CancellationToken ct)
    {
        var key = ResolveApiKey() ?? throw new InvalidDataException("game_provider_unavailable");
        // Keep the endpoint fixed: never follow a provider-supplied next URL.
        var url = $"https://api.rawg.io/api/games?key={Uri.EscapeDataString(key)}&search={Uri.EscapeDataString(title)}&page_size=25&page=1";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Options.Set(TransientHttpRetryHandler.DisableRetries, true);
        var watch = Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        _stats.RecordExternal(ExternalProviderKeys.Rawg, response.IsSuccessStatusCode, watch.ElapsedMilliseconds);
        if (!response.IsSuccessStatusCode) throw new InvalidDataException("game_provider_unavailable");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return json.RootElement.Clone();
    }
}
