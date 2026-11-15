using System.Text.Json;
using Feedarr.Api.Services.Matching;
using System.Diagnostics;
using Feedarr.Api.Services.Resilience;

namespace Feedarr.Api.Services.TheAudioDb;

public sealed partial class TheAudioDbClient
{
    internal bool MatchingEnabled => ResolveCreds() is not null;
    internal async Task<IReadOnlyList<AudioObservation>> CollectMatchingAsync(string title, string artist, CancellationToken ct)
    {
        var key = ResolveCreds() ?? throw new InvalidDataException("audio_provider_unavailable");
        var found = new List<AudioObservation>();
        foreach (var kind in new[] { "track", "album" })
        {
            var path = $"search{kind}.php?s={Uri.EscapeDataString(artist)}&{(kind == "track" ? "t" : "a")}={Uri.EscapeDataString(title)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildEndpoint(key, path));
            request.Options.Set(TransientHttpRetryHandler.DisableRetries, true);
            var watch = Stopwatch.StartNew();
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            _stats.RecordExternal(Feedarr.Api.Services.ExternalProviders.ExternalProviderKeys.TheAudioDb, response.IsSuccessStatusCode, watch.ElapsedMilliseconds);
            if (!response.IsSuccessStatusCode) throw new InvalidDataException("audio_provider_unavailable");
            var json = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).TrimStart('\uFEFF');
            using var document = JsonDocument.Parse(json);
            found.AddRange(AudioCandidateAdapter.FromAudioDb(document.RootElement, kind));
        }
        return found;
    }
}
