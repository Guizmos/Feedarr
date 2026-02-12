using System.Text.Json;
using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Services.Jackett;

public sealed class JackettClient
{
    private readonly HttpClient _http;

    public JackettClient(HttpClient http)
    {
        _http = http;
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        if (!OutboundUrlGuard.TryNormalizeProviderBaseUrl("jackett", baseUrl, out var normalizedBaseUrl, out var error))
            throw new ArgumentException(error, nameof(baseUrl));
        return normalizedBaseUrl;
    }

    private static string BuildIndexersUrl(string baseUrl, string apiKey)
    {
        var baseTrim = NormalizeBaseUrl(baseUrl);
        var url = $"{baseTrim}/api/v2.0/indexers";
        var uri = new UriBuilder(url);
        var query = $"apikey={Uri.EscapeDataString(apiKey ?? "")}&configured=true";
        if (!string.IsNullOrWhiteSpace(uri.Query))
        {
            var existing = uri.Query.TrimStart('?');
            if (!string.IsNullOrWhiteSpace(existing))
                query = $"{existing}&{query}";
        }
        uri.Query = query;
        return uri.ToString();
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

    private async Task<List<(string id, string name, string torznabUrl)>> ListIndexersCoreAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        var url = BuildIndexersUrl(baseUrl, apiKey);
        using var resp = await _http.GetAsync(url, ct);
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
        var configuredFlagsSeen = 0;

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var configured = GetBool(el, "configured")
                ?? GetBool(el, "isConfigured")
                ?? GetBool(el, "enabled")
                ?? GetBool(el, "isEnabled");
            if (configured.HasValue)
                configuredFlagsSeen++;
            if (configured.HasValue && configured.Value == false)
                continue;

            var id = GetString(el, "id") ?? GetString(el, "identifier") ?? GetString(el, "name");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var name = GetString(el, "name") ?? GetString(el, "title") ?? id;
            var torznabUrl = $"{baseTrim}/api/v2.0/indexers/{id}/results/torznab/";
            results.Add((id, name ?? id, torznabUrl));
        }

        if (configuredFlagsSeen == 0)
        {
            return results;
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
