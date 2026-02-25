using System.Text.Json;
using System.Xml.Linq;
using Feedarr.Api.Services.Resilience;
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

    // ── URL builders ────────────────────────────────────────────────

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

    private static string BuildTorznabIndexersUrl(string baseUrl, string apiKey)
    {
        var baseTrim = NormalizeBaseUrl(baseUrl);
        return $"{baseTrim}/api/v2.0/indexers/all/results/torznab/api?t=indexers&apikey={Uri.EscapeDataString(apiKey ?? "")}";
    }

    private async Task<HttpResponseMessage> SendGetAllowingSameHostDowngradeAsync(string url, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Options.Set(ProtocolDowngradeRedirectHandler.AllowHttpsToHttpDowngradeOption, true);
        return await _http.SendAsync(request, ct);
    }

    // ── JSON helpers ────────────────────────────────────────────────

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

    // ── Strategy 1: Management API (JSON) ───────────────────────────

    private async Task<List<(string id, string name, string torznabUrl)>> ListViaManagementApiAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        var url = BuildIndexersUrl(baseUrl, apiKey);
        using var resp = await SendGetAllowingSameHostDowngradeAsync(url, ct);

        // If Jackett redirects to login (302) or returns error, let it throw
        resp.EnsureSuccessStatusCode();

        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!string.IsNullOrWhiteSpace(contentType) &&
            !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            throw new JsonException($"Not JSON (content-type={contentType})");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return new List<(string id, string name, string torznabUrl)>();

        var baseTrim = NormalizeBaseUrl(baseUrl);
        var results = new List<(string id, string name, string torznabUrl)>();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var configured = GetBool(el, "configured")
                ?? GetBool(el, "isConfigured")
                ?? GetBool(el, "enabled")
                ?? GetBool(el, "isEnabled");
            if (configured.HasValue && configured.Value == false)
                continue;

            var id = GetString(el, "id") ?? GetString(el, "identifier") ?? GetString(el, "name");
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (id.Equals("all", StringComparison.OrdinalIgnoreCase)) continue;

            var name = GetString(el, "name") ?? GetString(el, "title") ?? id;
            var torznabUrl = $"{baseTrim}/api/v2.0/indexers/{id}/results/torznab/";
            results.Add((id, name ?? id, torznabUrl));
        }

        return results;
    }

    // ── Strategy 2: Torznab t=indexers (XML) ────────────────────────
    // Works behind reverse proxies where the management API is blocked.

    private async Task<List<(string id, string name, string torznabUrl)>> ListViaTorznabAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        var url = BuildTorznabIndexersUrl(baseUrl, apiKey);
        using var resp = await SendGetAllowingSameHostDowngradeAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        var xml = await resp.Content.ReadAsStringAsync(ct);
        var doc = XDocument.Parse(xml);
        var baseTrim = NormalizeBaseUrl(baseUrl);
        var results = new List<(string id, string name, string torznabUrl)>();

        foreach (var el in doc.Descendants("indexer"))
        {
            var configured = el.Attribute("configured")?.Value;
            if (!string.Equals(configured, "true", StringComparison.OrdinalIgnoreCase))
                continue;

            var id = el.Attribute("id")?.Value;
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (id.Equals("all", StringComparison.OrdinalIgnoreCase)) continue;

            var name = el.Element("title")?.Value ?? id;
            var torznabUrl = $"{baseTrim}/api/v2.0/indexers/{id}/results/torznab/";
            results.Add((id, name, torznabUrl));
        }

        return results;
    }

    // ── Public entry point ──────────────────────────────────────────

    public async Task<List<(string id, string name, string torznabUrl)>> ListIndexersAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        // Try management API first (works for direct HTTP access)
        try
        {
            return await ListViaManagementApiAsync(baseUrl, apiKey, ct);
        }
        catch
        {
            // Management API failed (redirect to login, 404, non-JSON, etc.)
            // Fall back to torznab t=indexers which works behind reverse proxies
        }

        return await ListViaTorznabAsync(baseUrl, apiKey, ct);
    }
}
