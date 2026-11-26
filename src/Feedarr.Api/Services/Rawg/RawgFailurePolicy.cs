using System.Text.Json;

namespace Feedarr.Api.Services.Rawg;

internal static class RawgFailurePolicy
{
    internal static async Task<string> ClassifyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // Strict allowlist, bounded body, no raw provider message retained or returned.
        var bytes = new byte[4097]; var read = 0;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        while (read < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(read), ct).ConfigureAwait(false);
            if (count == 0) break;
            read += count;
        }
        if (read <= 4096)
        {
            try
            {
                using var doc = JsonDocument.Parse(bytes.AsMemory(0, read));
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                {
                    var message = error.GetString()?.Trim();
                    if (string.Equals(message, "The monthly API limit reached", StringComparison.OrdinalIgnoreCase)) return "rawg_quota_exhausted";
                    if ((int)response.StatusCode == 401 && message is "Invalid API key" or "The API key is not found" or "API key is invalid") return "rawg_invalid_api_key";
                }
            }
            catch (JsonException) { }
        }
        return (int)response.StatusCode switch {
            401 => "rawg_http_unauthorized", 403 => "rawg_http_forbidden", 429 => "rawg_http_rate_limited",
            >= 500 => "rawg_http_server_error", >= 400 => "rawg_http_client_error", _ => "rawg_http_error"
        };
    }
}
