using System.Text.Json;
using System.Xml.Linq;
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

    private static string BuildAllTorznabUrl(string baseUrl)
    {
        var baseTrim = NormalizeBaseUrl(baseUrl);
        return $"{baseTrim}/api/v2.0/indexers/all/results/torznab/api";
    }

    private static string BuildAllCapsUrl(string baseUrl, string apiKey)
    {
        var url = BuildAllTorznabUrl(baseUrl);
        var uri = new UriBuilder(url);
        var query = $"t=caps&apikey={Uri.EscapeDataString(apiKey ?? "")}";
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

        // AllowAutoRedirect=false : on reçoit les 3xx directement.
        // Un 302 vers /UI/Login signifie que l'API key n'est pas acceptée
        // par cette route (fréquent derrière un reverse proxy HTTPS).
        var sc = (int)resp.StatusCode;
        if (sc >= 300 && sc < 400)
        {
            var location = resp.Headers.Location?.ToString() ?? "";
            throw new HttpRequestException(
                $"Redirect {sc} → {location} (tentative fallback torznab)");
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

    private async Task<bool> ValidateAllCapsAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        var url = BuildAllCapsUrl(baseUrl, apiKey);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            return false;

        var payload = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            var doc = XDocument.Parse(payload);
            return doc.Descendants().Any(x => x.Name.LocalName.Equals("caps", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static List<(string id, string name, string torznabUrl)> BuildAllFallbackIndexer(string baseUrl)
    {
        return new List<(string id, string name, string torznabUrl)>
        {
            ("all", "All Indexers", BuildAllTorznabUrl(baseUrl))
        };
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
            try
            {
                return await ListIndexersCoreAsync(baseUrl, apiKey, ct);
            }
            catch (Exception secondEx) when (CanFallbackToCaps(secondEx))
            {
                if (await ValidateAllCapsAsync(baseUrl, apiKey, ct))
                    return BuildAllFallbackIndexer(baseUrl);
                throw;
            }
        }
        catch (Exception ex) when (CanFallbackToCaps(ex))
        {
            if (await ValidateAllCapsAsync(baseUrl, apiKey, ct))
                return BuildAllFallbackIndexer(baseUrl);
            throw;
        }
    }

    private static bool CanFallbackToCaps(Exception ex)
    {
        return ex is JsonException ||
               ex is HttpRequestException ||
               ex is TaskCanceledException ||
               ex is InvalidOperationException;
    }

    private static bool ShouldRetryJson(Exception ex)
    {
        var msg = ex.Message ?? "";
        return msg.Contains("invalid start of a value", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("unexpected token", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("invalid json response", StringComparison.OrdinalIgnoreCase);
    }
}
