using System.Text.Json;
using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Services.Prowlarr;

public sealed class ProwlarrClient
{
    private readonly HttpClient _http;

    public ProwlarrClient(HttpClient http)
    {
        _http = http;
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        if (!OutboundUrlGuard.TryNormalizeProviderBaseUrl("prowlarr", baseUrl, out var normalizedBaseUrl, out var error))
            throw new ArgumentException(error, nameof(baseUrl));
        return normalizedBaseUrl;
    }

    private static string BuildIndexersUrl(string baseUrl)
    {
        var baseTrim = NormalizeBaseUrl(baseUrl);
        return $"{baseTrim}/api/v1/indexer";
    }

    private static bool? GetBool(JsonElement element, string prop)
    {
        if (!element.TryGetProperty(prop, out var val)) return null;
        if (val.ValueKind == JsonValueKind.True) return true;
        if (val.ValueKind == JsonValueKind.False) return false;
        if (val.ValueKind == JsonValueKind.String && bool.TryParse(val.GetString(), out var b)) return b;
        return null;
    }

    private static string? GetString(JsonElement element, string prop)
    {
        if (!element.TryGetProperty(prop, out var val)) return null;
        if (val.ValueKind == JsonValueKind.String) return val.GetString();
        if (val.ValueKind == JsonValueKind.Number) return val.GetRawText();
        return null;
    }

    private static int? GetInt(JsonElement element, string prop)
    {
        if (!element.TryGetProperty(prop, out var val)) return null;
        if (val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out var i)) return i;
        if (val.ValueKind == JsonValueKind.String && int.TryParse(val.GetString(), out var parsed)) return parsed;
        return null;
    }

    private async Task<List<(string id, string name, string torznabUrl)>> ListIndexersCoreAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        var url = BuildIndexersUrl(baseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Api-Key", apiKey);

        using var resp = await _http.SendAsync(request, ct);

        // AllowAutoRedirect=false : gérer les 3xx explicitement
        var sc = (int)resp.StatusCode;
        if (sc >= 300 && sc < 400)
        {
            var location = resp.Headers.Location?.ToString() ?? "";
            throw new HttpRequestException(
                $"Redirect {sc} → {location}");
        }

        resp.EnsureSuccessStatusCode();

        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!string.IsNullOrWhiteSpace(contentType) &&
            !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            throw new JsonException($"Invalid JSON response (content-type={contentType})");
        }

        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return new List<(string id, string name, string torznabUrl)>();

        var baseTrim = NormalizeBaseUrl(baseUrl);
        var results = new List<(string id, string name, string torznabUrl)>();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            // Filter: enable == true
            var enable = GetBool(el, "enable");
            if (enable.HasValue && enable.Value == false)
                continue;

            // Filter: protocol == "torrent"
            var protocol = GetString(el, "protocol");
            if (!string.IsNullOrEmpty(protocol) &&
                !protocol.Equals("torrent", StringComparison.OrdinalIgnoreCase))
                continue;

            // Get indexer id (integer in Prowlarr)
            var indexerId = GetInt(el, "id");
            if (!indexerId.HasValue || indexerId.Value <= 0)
                continue;

            var id = indexerId.Value.ToString();
            var name = GetString(el, "name") ?? id;

            // Prowlarr Torznab URL format: {baseUrl}/{id}/api
            var torznabUrl = $"{baseTrim}/{id}/api";
            results.Add((id, name, torznabUrl));
        }

        return results;
    }

    public async Task<List<(string id, string name, string torznabUrl)>> ListIndexersAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        try
        {
            return await ListIndexersCoreAsync(baseUrl, apiKey, ct);
        }
        catch (JsonException ex) when (ShouldRetryJson(ex))
        {
            await Task.Delay(350, ct);
            return await ListIndexersCoreAsync(baseUrl, apiKey, ct);
        }
    }

    private static bool ShouldRetryJson(Exception ex)
    {
        var msg = ex.Message ?? "";
        return msg.Contains("invalid start of a value", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("unexpected token", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("invalid json response", StringComparison.OrdinalIgnoreCase);
    }
}
