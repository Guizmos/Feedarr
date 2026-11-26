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
        var response = await _matchingResponses.GetAsync($"evidence:{key}:{GetMediaInfoLanguage()}:{path}", async token => {
            var value = await GetTelevisionEvidenceCoreAsync(path, token).ConfigureAwait(false);
            return value is { } json ? new Feedarr.Api.Services.Matching.VideoEvidenceResponse(json) : null;
        }, value => Feedarr.Api.Services.Matching.VideoEvidenceResponse.Complete("tmdb", path, value.Value), ct).ConfigureAwait(false);
        return response?.Value;
    }

    private TvDetailsResponse? PublicationTvEvidence(int id)
    {
        var credential = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(GetApiKeyOrThrow())));
        var evidence = _matchingResponses.Peek<Feedarr.Api.Services.Matching.VideoEvidenceResponse>(
            $"evidence:{credential}:{GetMediaInfoLanguage()}:tv/{id}?append_to_response=external_ids,alternative_titles");
        if (evidence?.Value is not { ValueKind: JsonValueKind.Object } root ||
            Feedarr.Api.Services.Matching.TelevisionCandidates.Number(root, "id") != id ||
            root.TryGetProperty("media_type", out var media) && (media.ValueKind != JsonValueKind.String || media.GetString() != "tv")) return null;
        // Every field consumed by publication must actually be present. Missing fields
        // trigger the existing localized details fetch, never a guessed/English snapshot.
        if (!new[] { "name", "overview", "tagline", "genres", "first_air_date", "episode_run_time", "vote_average", "vote_count", "poster_path" }
            .All(f => root.TryGetProperty(f, out _))) return null;
        try
        {
            var details = JsonSerializer.Deserialize<TvDetailsResponse>(root.GetRawText(), JsonOpts);
            return string.IsNullOrWhiteSpace(details?.Name) ? null : details;
        }
        catch (JsonException) { return null; }
    }

    private async Task<JsonElement?> GetTelevisionEvidenceCoreAsync(string path, CancellationToken ct)
    {
        var separator = path.Contains('?') ? '&' : '?';
        path += separator + "language=" + Uri.EscapeDataString(GetMediaInfoLanguage());
        separator = '&';
        try { return await GetJsonAsync<JsonElement>(path + separator + "api_key=" + Uri.EscapeDataString(GetApiKeyOrThrow()), ct, disableRetries: true).ConfigureAwait(false); }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { return null; }
    }
}
