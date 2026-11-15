using System.Text.Json;
using Feedarr.Api.Services.Resilience;
namespace Feedarr.Api.Services.ComicVine;

public sealed partial class ComicVineClient
{
    internal async Task<JsonElement?> ReadMatchingAsync(string title, string? isbn, string? author, CancellationToken ct)
    {
        var key = ResolveCreds();
        if (key is null) return null;
        var endpoint = BuildSearchEndpoint(key, title);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Options.Set(TransientHttpRetryHandler.DisableRetries, true);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        _stats.RecordExternal(Feedarr.Api.Services.ExternalProviders.ExternalProviderKeys.ComicVine, response.IsSuccessStatusCode, watch.ElapsedMilliseconds);
        if (!response.IsSuccessStatusCode) throw new InvalidDataException("print_provider_unavailable");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return document.RootElement.Clone();
    }
}
