using System.Net;
using System.Text;
using System.Text.Json;
using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Services.Arr;

public sealed class EerrRequestClient
{
    private readonly HttpClient _http;

    public EerrRequestClient(HttpClient http)
    {
        _http = http;
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        if (!OutboundUrlGuard.TryNormalizeArrBaseUrl(baseUrl, out var normalizedBaseUrl, out var error))
            throw new ArgumentException(error, nameof(baseUrl));
        return normalizedBaseUrl;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string baseUrl, string endpoint, string apiKey)
    {
        var url = $"{NormalizeBaseUrl(baseUrl)}/api/v1/{endpoint.TrimStart('/')}";
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Api-Key", apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    public async Task<(bool ok, string? version, string? appName, string? error)> TestConnectionAsync(
        string baseUrl,
        string apiKey,
        string appType,
        CancellationToken ct)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, baseUrl, "status", apiKey);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return (false, null, null, $"HTTP {(int)response.StatusCode}");

            var json = await response.Content.ReadAsStringAsync(ct);
            return (true, ExtractVersion(json), GetAppLabel(appType), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, null, null, "request service timeout");
        }
        catch (HttpRequestException)
        {
            return (false, null, null, "request service unavailable");
        }
        catch (JsonException)
        {
            return (false, null, null, "invalid request service response");
        }
        catch (ArgumentException)
        {
            return (false, null, null, "baseUrl invalid");
        }
        catch
        {
            return (false, null, null, "connection failed");
        }
    }

    public async Task<EerrCreateRequestResult> CreateRequestAsync(
        string baseUrl,
        string apiKey,
        string mediaType,
        int mediaId,
        CancellationToken ct,
        int? tvdbId = null)
    {
        var normalizedMediaType = NormalizeMediaType(mediaType);
        if (normalizedMediaType is null)
        {
            return new EerrCreateRequestResult
            {
                Success = false,
                Status = "error",
                Message = "mediaType must be movie or tv"
            };
        }

        // Overseerr/Jellyseerr TV requests are more reliable when explicitly requesting all seasons.
        object payloadObject;
        if (normalizedMediaType == "tv")
        {
            payloadObject = tvdbId.HasValue && tvdbId.Value > 0
                ? new
                {
                    mediaType = normalizedMediaType,
                    mediaId,
                    tvdbId = tvdbId.Value,
                    seasons = "all"
                }
                : new
                {
                    mediaType = normalizedMediaType,
                    mediaId,
                    seasons = "all"
                };
        }
        else
        {
            payloadObject = new
            {
                mediaType = normalizedMediaType,
                mediaId
            };
        }

        var payload = JsonSerializer.Serialize(payloadObject);

        using var request = CreateRequest(HttpMethod.Post, baseUrl, "request", apiKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var requestId = ExtractRequestId(body);

        if (response.IsSuccessStatusCode)
        {
            return new EerrCreateRequestResult
            {
                Success = true,
                Status = "added",
                RequestId = requestId,
                OpenUrl = requestId.HasValue ? BuildRequestUrl(baseUrl, requestId.Value) : BuildRequestsUrl(baseUrl)
            };
        }

        if (response.StatusCode == HttpStatusCode.Conflict ||
            body.Contains("already requested", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return new EerrCreateRequestResult
            {
                Success = true,
                Status = "exists",
                RequestId = requestId,
                OpenUrl = requestId.HasValue ? BuildRequestUrl(baseUrl, requestId.Value) : BuildRequestsUrl(baseUrl),
                Message = "Request already exists"
            };
        }

        return new EerrCreateRequestResult
        {
            Success = false,
            Status = "error",
            RequestId = requestId,
            Message = $"HTTP {(int)response.StatusCode}"
        };
    }

    public async Task<List<EerrRequestEntry>> GetRequestsAsync(
        string baseUrl,
        string apiKey,
        CancellationToken ct,
        int maxItems = 2000)
    {
        var requests = new List<EerrRequestEntry>();
        var take = 100;
        var skip = 0;
        var safeMax = Math.Max(100, maxItems);

        while (requests.Count < safeMax)
        {
            using var request = CreateRequest(HttpMethod.Get, baseUrl, $"request?take={take}&skip={skip}", apiKey);
            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            var page = ParseRequestPage(body);
            if (page.Results.Count == 0)
                break;

            requests.AddRange(page.Results);
            skip += page.Results.Count;

            if (page.TotalResults.HasValue && skip >= page.TotalResults.Value)
                break;
            if (page.Results.Count < take && !page.TotalResults.HasValue)
                break;
        }

        if (requests.Count > safeMax)
            return requests.Take(safeMax).ToList();
        return requests;
    }

    public string BuildRequestUrl(string baseUrl, int requestId)
        => $"{NormalizeBaseUrl(baseUrl)}/requests/{requestId}";

    public string BuildRequestsUrl(string baseUrl)
        => $"{NormalizeBaseUrl(baseUrl)}/requests";

    public static string GetAppLabel(string appType)
    {
        var type = (appType ?? string.Empty).Trim().ToLowerInvariant();
        return type == "jellyseerr" ? "Jellyseerr" : "Overseerr";
    }

    private static string? NormalizeMediaType(string mediaType)
    {
        var normalized = (mediaType ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is "movie" or "film")
            return "movie";
        if (normalized is "tv" or "series" or "show")
            return "tv";
        return null;
    }

    private static string? ExtractVersion(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (TryGetString(root, "version", out var version))
            return version;
        if (root.TryGetProperty("appData", out var appData) &&
            appData.ValueKind == JsonValueKind.Object &&
            TryGetString(appData, "version", out var nestedVersion))
        {
            return nestedVersion;
        }

        return null;
    }

    private static int? ExtractRequestId(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryGetInt(root, "id", out var id)) return id;
                if (root.TryGetProperty("request", out var req) &&
                    req.ValueKind == JsonValueKind.Object &&
                    TryGetInt(req, "id", out var nestedId))
                {
                    return nestedId;
                }
            }
        }
        catch
        {
            // Ignore parse errors and return null fallback
        }

        return null;
    }

    private static EerrRequestPage ParseRequestPage(string json)
    {
        var page = new EerrRequestPage();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            page.Results = ParseResults(root);
            page.TotalResults = page.Results.Count;
            return page;
        }

        if (root.ValueKind != JsonValueKind.Object)
            return page;

        if (root.TryGetProperty("results", out var resultsNode) &&
            resultsNode.ValueKind == JsonValueKind.Array)
        {
            page.Results = ParseResults(resultsNode);
        }

        if (root.TryGetProperty("pageInfo", out var pageInfo) &&
            pageInfo.ValueKind == JsonValueKind.Object &&
            TryGetInt(pageInfo, "totalResults", out var totalResults))
        {
            page.TotalResults = totalResults;
        }
        else if (TryGetInt(root, "totalResults", out var flatTotal))
        {
            page.TotalResults = flatTotal;
        }

        return page;
    }

    private static List<EerrRequestEntry> ParseResults(JsonElement arrayNode)
    {
        var rows = new List<EerrRequestEntry>();
        foreach (var row in arrayNode.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
                continue;

            if (!TryGetInt(row, "id", out var requestId))
                continue;

            var mediaType = "";
            if (TryGetString(row, "type", out var type)) mediaType = type;
            else if (TryGetString(row, "mediaType", out var mediaTypeValue)) mediaType = mediaTypeValue;

            int? tmdbId = null;
            int? tvdbId = null;
            if (row.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Object)
            {
                if (TryGetInt(media, "tmdbId", out var mediaTmdb)) tmdbId = mediaTmdb;
                else if (TryGetInt(media, "mediaId", out var mediaId)) tmdbId = mediaId;
                if (TryGetInt(media, "tvdbId", out var mediaTvdb)) tvdbId = mediaTvdb;
            }

            if (!tmdbId.HasValue && TryGetInt(row, "mediaId", out var rootMediaId))
                tmdbId = rootMediaId;

            var normalizedType = NormalizeMediaType(mediaType ?? string.Empty) ?? (tvdbId.HasValue ? "tv" : "movie");
            rows.Add(new EerrRequestEntry
            {
                RequestId = requestId,
                MediaType = normalizedType,
                TmdbId = tmdbId,
                TvdbId = tvdbId
            });
        }

        return rows;
    }

    private static bool TryGetInt(JsonElement obj, string propertyName, out int value)
    {
        value = 0;
        if (!obj.TryGetProperty(propertyName, out var node))
            return false;

        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out value))
            return true;

        if (node.ValueKind == JsonValueKind.String &&
            int.TryParse(node.GetString(), out value))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetString(JsonElement obj, string propertyName, out string? value)
    {
        value = null;
        if (!obj.TryGetProperty(propertyName, out var node))
            return false;
        if (node.ValueKind != JsonValueKind.String)
            return false;

        value = node.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private sealed class EerrRequestPage
    {
        public List<EerrRequestEntry> Results { get; set; } = new();
        public int? TotalResults { get; set; }
    }
}

public sealed class EerrCreateRequestResult
{
    public bool Success { get; set; }
    public string Status { get; set; } = "";
    public int? RequestId { get; set; }
    public string? Message { get; set; }
    public string? OpenUrl { get; set; }
}

public sealed class EerrRequestEntry
{
    public int RequestId { get; set; }
    public string MediaType { get; set; } = "";
    public int? TmdbId { get; set; }
    public int? TvdbId { get; set; }
}
