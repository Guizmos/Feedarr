using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Feedarr.Api.Services.Arr;

public sealed class SonarrClient
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SonarrClient(HttpClient http)
    {
        _http = http;
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        var url = (baseUrl ?? "").Trim().TrimEnd('/');
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "http://" + url;
        }
        return url;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string baseUrl, string endpoint, string apiKey)
    {
        var url = $"{NormalizeBaseUrl(baseUrl)}/api/v3/{endpoint.TrimStart('/')}";
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Api-Key", apiKey);
        return request;
    }

    public async Task<(bool ok, string? version, string? appName, string? error)> TestConnectionAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, baseUrl, "system/status", apiKey);
            using var response = await _http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                return (false, null, null, $"HTTP {(int)response.StatusCode}: {errorBody}");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var status = JsonSerializer.Deserialize<SystemStatusResponse>(json, JsonOpts);
            return (true, status?.Version, status?.AppName ?? "Sonarr", null);
        }
        catch (Exception ex)
        {
            return (false, null, null, ex.Message);
        }
    }

    public async Task<List<SeriesLookupResult>> LookupSeriesAsync(
        string baseUrl, string apiKey, int tvdbId, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, $"series/lookup?term=tvdb:{tvdbId}", apiKey);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<SeriesLookupResult>>(json, JsonOpts) ?? new();
    }

    public async Task<List<SeriesResult>> GetAllSeriesAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, "series", apiKey);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<SeriesResult>>(json, JsonOpts) ?? new();
    }

    public async Task<SeriesResult?> GetSeriesByTvdbIdAsync(
        string baseUrl, string apiKey, int tvdbId, CancellationToken ct)
    {
        var all = await GetAllSeriesAsync(baseUrl, apiKey, ct);
        return all.FirstOrDefault(s => s.TvdbId == tvdbId);
    }

    public async Task<AddSeriesResult> AddSeriesAsync(
        string baseUrl,
        string apiKey,
        SeriesLookupResult lookup,
        string rootFolderPath,
        int qualityProfileId,
        List<int>? tags,
        string seriesType,
        bool seasonFolder,
        string monitorMode,
        bool searchMissing,
        bool searchCutoff,
        CancellationToken ct)
    {
        var payload = new AddSeriesPayload
        {
            TvdbId = lookup.TvdbId,
            Title = lookup.Title,
            TitleSlug = lookup.TitleSlug,
            Images = lookup.Images,
            Seasons = lookup.Seasons,
            RootFolderPath = rootFolderPath,
            QualityProfileId = qualityProfileId,
            Tags = tags ?? new(),
            SeriesType = seriesType,
            SeasonFolder = seasonFolder,
            Monitored = true,
            AddOptions = new AddSeriesOptions
            {
                Monitor = monitorMode,
                SearchForMissingEpisodes = searchMissing,
                SearchForCutoffUnmetEpisodes = searchCutoff
            }
        };

        using var request = CreateRequest(HttpMethod.Post, baseUrl, "series", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOpts),
            Encoding.UTF8,
            "application/json"
        );

        using var response = await _http.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == HttpStatusCode.Created || response.IsSuccessStatusCode)
        {
            var series = JsonSerializer.Deserialize<SeriesResult>(responseBody, JsonOpts);
            return new AddSeriesResult
            {
                Success = true,
                Status = "added",
                SeriesId = series?.Id,
                TitleSlug = series?.TitleSlug ?? lookup.TitleSlug,
                Message = null
            };
        }

        if (response.StatusCode == HttpStatusCode.Conflict ||
            responseBody.Contains("already been added", StringComparison.OrdinalIgnoreCase) ||
            responseBody.Contains("SeriesExistsValidator", StringComparison.OrdinalIgnoreCase))
        {
            var existing = await GetSeriesByTvdbIdAsync(baseUrl, apiKey, lookup.TvdbId, ct);
            return new AddSeriesResult
            {
                Success = true,
                Status = "exists",
                SeriesId = existing?.Id,
                TitleSlug = existing?.TitleSlug ?? lookup.TitleSlug,
                Message = "Series already exists in Sonarr"
            };
        }

        return new AddSeriesResult
        {
            Success = false,
            Status = "error",
            SeriesId = null,
            Message = $"HTTP {(int)response.StatusCode}: {responseBody}"
        };
    }

    public async Task<List<RootFolderResult>> GetRootFoldersAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, "rootfolder", apiKey);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<RootFolderResult>>(json, JsonOpts) ?? new();
    }

    public async Task<List<QualityProfileResult>> GetQualityProfilesAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, "qualityprofile", apiKey);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<QualityProfileResult>>(json, JsonOpts) ?? new();
    }

    public async Task<List<TagResult>> GetTagsAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, "tag", apiKey);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<TagResult>>(json, JsonOpts) ?? new();
    }

    public string BuildOpenUrl(string baseUrl, string titleSlug)
    {
        return $"{NormalizeBaseUrl(baseUrl)}/series/{titleSlug}";
    }

    // DTOs
    private sealed class SystemStatusResponse
    {
        public string? Version { get; set; }
        public string? AppName { get; set; }
    }

    public sealed class SeriesLookupResult
    {
        public int TvdbId { get; set; }
        public string Title { get; set; } = "";
        public string TitleSlug { get; set; } = "";
        public List<ImageItem>? Images { get; set; }
        public List<SeasonItem>? Seasons { get; set; }
        public int Year { get; set; }
    }

    public sealed class SeriesResult
    {
        public int Id { get; set; }
        public int TvdbId { get; set; }
        public string Title { get; set; } = "";
        public string TitleSlug { get; set; } = "";
        public List<AlternateTitleItem>? AlternateTitles { get; set; }
    }

    public sealed class AlternateTitleItem
    {
        public string Title { get; set; } = "";
        public int? SeasonNumber { get; set; }
    }

    public sealed class ImageItem
    {
        public string CoverType { get; set; } = "";
        public string Url { get; set; } = "";
        public string RemoteUrl { get; set; } = "";
    }

    public sealed class SeasonItem
    {
        public int SeasonNumber { get; set; }
        public bool Monitored { get; set; }
    }

    public sealed class AddSeriesPayload
    {
        public int TvdbId { get; set; }
        public string Title { get; set; } = "";
        public string TitleSlug { get; set; } = "";
        public List<ImageItem>? Images { get; set; }
        public List<SeasonItem>? Seasons { get; set; }
        public string RootFolderPath { get; set; } = "";
        public int QualityProfileId { get; set; }
        public List<int> Tags { get; set; } = new();
        public string SeriesType { get; set; } = "standard";
        public bool SeasonFolder { get; set; } = true;
        public bool Monitored { get; set; } = true;
        public AddSeriesOptions? AddOptions { get; set; }
    }

    public sealed class AddSeriesOptions
    {
        public string Monitor { get; set; } = "all";
        public bool SearchForMissingEpisodes { get; set; } = true;
        public bool SearchForCutoffUnmetEpisodes { get; set; }
    }

    public sealed class AddSeriesResult
    {
        public bool Success { get; set; }
        public string Status { get; set; } = "";
        public int? SeriesId { get; set; }
        public string? TitleSlug { get; set; }
        public string? Message { get; set; }
    }

    public sealed class RootFolderResult
    {
        public int Id { get; set; }
        public string Path { get; set; } = "";
        public long FreeSpace { get; set; }
    }

    public sealed class QualityProfileResult
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class TagResult
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
    }
}
