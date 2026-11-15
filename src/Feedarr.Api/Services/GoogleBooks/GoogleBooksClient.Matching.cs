using System.Text.Json;
using Feedarr.Api.Services.Resilience;
namespace Feedarr.Api.Services.GoogleBooks;

public sealed partial class GoogleBooksClient
{
    internal async Task<JsonElement?> ReadMatchingAsync(string title, string? isbn, string? author, CancellationToken ct)
    {
        var active = _resolver.Resolve(Feedarr.Api.Services.ExternalProviders.ExternalProviderKeys.GoogleBooks);
        if (!active.Enabled) return null;
        var query = isbn is not null ? $"isbn:{isbn}" : $"intitle:{title}" + (author is null ? "" : $" inauthor:{author}");
        var endpoint = BuildVolumesEndpoint(query, active.Auth.TryGetValue("apiKey", out var key) ? key : null);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Options.Set(TransientHttpRetryHandler.DisableRetries, true);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        _stats.RecordExternal(Feedarr.Api.Services.ExternalProviders.ExternalProviderKeys.GoogleBooks, response.IsSuccessStatusCode, watch.ElapsedMilliseconds);
        if (!response.IsSuccessStatusCode) throw new InvalidDataException("print_provider_unavailable");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return document.RootElement.Clone();
    }
}
