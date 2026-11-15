using System.Text.Json;
using Feedarr.Api.Services.Resilience;
namespace Feedarr.Api.Services.OpenLibrary;

public sealed partial class OpenLibraryClient
{
    internal async Task<JsonElement?> ReadMatchingAsync(string title, string? isbn, string? author, CancellationToken ct)
    {
        var endpoint = isbn is not null ? $"{OlBaseUrl}isbn/{Uri.EscapeDataString(isbn)}.json" :
            $"{OlBaseUrl}search.json?title={Uri.EscapeDataString(title)}&limit=10&fields=key,title,alternative_title,author_name,first_publish_year,cover_i,language" +
            (author is null ? "" : $"&author={Uri.EscapeDataString(author)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Options.Set(TransientHttpRetryHandler.DisableRetries, true);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        _stats.RecordExternal(Feedarr.Api.Services.ExternalProviders.ExternalProviderKeys.OpenLibrary, response.IsSuccessStatusCode, watch.ElapsedMilliseconds);
        if (isbn is not null && response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new InvalidDataException("print_provider_unavailable");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return document.RootElement.Clone();
    }
}
