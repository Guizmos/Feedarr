using System.Net;
using System.Text.Json;
namespace Feedarr.Api.Services.Tmdb;
public sealed partial class TmdbClient
{
    internal bool MatchingConfigured => !string.IsNullOrWhiteSpace(TryGetApiKey());

    internal async Task<JsonElement?> GetTelevisionEvidenceAsync(string path, CancellationToken ct)
    {
        if (!path.StartsWith("tv/", StringComparison.Ordinal)) throw new ArgumentException(nameof(path));
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(GetApiKeyOrThrow())));
        var response = await _matchingResponses.GetAsync($"evidence:{key}:{path}", async token => {
            var value = await GetTelevisionEvidenceCoreAsync(path, token).ConfigureAwait(false);
            return value is { } json ? new Feedarr.Api.Services.Matching.VideoEvidenceResponse(json) : null;
        }, value => Feedarr.Api.Services.Matching.VideoEvidenceResponse.Complete("tmdb", path, value.Value), ct).ConfigureAwait(false);
        return response?.Value;
    }

    private async Task<JsonElement?> GetTelevisionEvidenceCoreAsync(string path, CancellationToken ct)
    {
        var separator = path.Contains('?') ? '&' : '?';
        try { return await GetJsonAsync<JsonElement>(path + separator + "api_key=" + Uri.EscapeDataString(GetApiKeyOrThrow()), ct, disableRetries: true).ConfigureAwait(false); }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { return null; }
    }
}
