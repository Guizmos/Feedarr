using System.Net;
using System.Text.Json;
namespace Feedarr.Api.Services.Tmdb;
public sealed partial class TmdbClient
{
    internal bool MatchingConfigured => !string.IsNullOrWhiteSpace(TryGetApiKey());

    internal async Task<JsonElement?> GetTelevisionEvidenceAsync(string path, CancellationToken ct)
    {
        if (!path.StartsWith("tv/", StringComparison.Ordinal)) throw new ArgumentException(nameof(path));
        var separator = path.Contains('?') ? '&' : '?';
        try { return await GetJsonAsync<JsonElement>(path + separator + "api_key=" + Uri.EscapeDataString(GetApiKeyOrThrow()), ct, disableRetries: true).ConfigureAwait(false); }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { return null; }
    }
}
