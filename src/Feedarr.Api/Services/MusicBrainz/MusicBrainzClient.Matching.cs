using System.Text.Json;
using System.Diagnostics;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Resilience;
using Feedarr.Api.Services.ExternalProviders;

namespace Feedarr.Api.Services.MusicBrainz;

public sealed partial class MusicBrainzClient
{
    internal bool MatchingEnabled => _resolver.Resolve(ExternalProviderKeys.MusicBrainz).Enabled;
    internal async Task<IReadOnlyList<AudioObservation>> CollectMatchingAsync(string title, string artist, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildSearchUrl(title, artist, 26));
        var (clientId, _) = ResolveCreds();
        if (!string.IsNullOrWhiteSpace(clientId)) request.Headers.TryAddWithoutValidation("X-Application", clientId);
        request.Options.Set(TransientHttpRetryHandler.DisableRetries, true);
        var watch = Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        _stats.RecordExternal(ExternalProviderKeys.MusicBrainz, response.IsSuccessStatusCode, watch.ElapsedMilliseconds);
        if (!response.IsSuccessStatusCode) throw new InvalidDataException("audio_provider_unavailable");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return AudioCandidateAdapter.FromMusicBrainz(document.RootElement);
    }
    // This endpoint always receives a RELEASE id, never an artist or release-group id.
    internal Task<string?> GetMatchingCoverAsync(string releaseId, CancellationToken ct)
    {
        if (!Guid.TryParse(releaseId, out _)) throw new InvalidDataException("audio_release_identity_invalid");
        return FetchCoverUrlAsync(releaseId, ct);
    }
}
