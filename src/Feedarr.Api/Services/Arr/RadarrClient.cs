using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Services.Arr;

public sealed class RadarrClient
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public RadarrClient(HttpClient http)
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
                return (false, null, null, $"HTTP {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var status = JsonSerializer.Deserialize<SystemStatusResponse>(json, JsonOpts);
            return (true, status?.Version, status?.AppName ?? "Radarr", null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, null, null, "arr service timeout");
        }
        catch (HttpRequestException)
        {
            return (false, null, null, "arr service unavailable");
        }
        catch (JsonException)
        {
            return (false, null, null, "invalid arr response");
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

    public async Task<List<MovieLookupResult>> LookupMovieAsync(
        string baseUrl, string apiKey, int tmdbId, CancellationToken ct)
    {
        // Radarr versions/proxies may not all support the same lookup route.
        // Try legacy query first, then fallback to dedicated tmdb lookup route.
        var legacy = await TryLookupMovieAsync(baseUrl, apiKey, $"movie/lookup?term=tmdb:{tmdbId}", ct);
        if (legacy.ok)
            return legacy.results;

        var fallback = await TryLookupMovieAsync(baseUrl, apiKey, $"movie/lookup/tmdb?tmdbId={tmdbId}", ct);
        if (fallback.ok)
            return fallback.results;

        throw new InvalidOperationException($"radarr lookup failed (HTTP {(int)(fallback.statusCode ?? legacy.statusCode ?? HttpStatusCode.BadGateway)})");
    }

    private async Task<(bool ok, List<MovieLookupResult> results, HttpStatusCode? statusCode)> TryLookupMovieAsync(
        string baseUrl, string apiKey, string endpoint, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, endpoint, apiKey);
        using var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return (false, new(), response.StatusCode);

        return (true, JsonSerializer.Deserialize<List<MovieLookupResult>>(json, JsonOpts) ?? new(), response.StatusCode);
    }

    public async Task<List<MovieResult>> GetAllMoviesAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, "movie", apiKey);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<MovieResult>>(json, JsonOpts) ?? new();
    }

    public async Task<MovieResult?> GetMovieByTmdbIdAsync(
        string baseUrl, string apiKey, int tmdbId, CancellationToken ct)
    {
        var all = await GetAllMoviesAsync(baseUrl, apiKey, ct);
        return all.FirstOrDefault(m => m.TmdbId == tmdbId);
    }

    public async Task<AddMovieResult> AddMovieAsync(
        string baseUrl,
        string apiKey,
        MovieLookupResult lookup,
        string rootFolderPath,
        int qualityProfileId,
        List<int>? tags,
        string minimumAvailability,
        bool searchForMovie,
        CancellationToken ct)
    {
        // Minimal payload as per requirement
        var payload = new AddMoviePayload
        {
            Title = lookup.Title,
            TmdbId = lookup.TmdbId,
            Year = lookup.Year,
            TitleSlug = lookup.TitleSlug,
            Images = lookup.Images,
            RootFolderPath = rootFolderPath,
            QualityProfileId = qualityProfileId,
            Tags = tags ?? new(),
            Monitored = true,
            MinimumAvailability = minimumAvailability,
            AddOptions = new AddMovieOptions
            {
                SearchForMovie = searchForMovie
            }
        };

        using var request = CreateRequest(HttpMethod.Post, baseUrl, "movie", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOpts),
            Encoding.UTF8,
            "application/json"
        );

        using var response = await _http.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == HttpStatusCode.Created || response.IsSuccessStatusCode)
        {
            var movie = JsonSerializer.Deserialize<MovieResult>(responseBody, JsonOpts);
            return new AddMovieResult
            {
                Success = true,
                Status = "added",
                MovieId = movie?.Id,
                Message = null
            };
        }

        // Handle "already exists" as success (400 MovieExistsValidator)
        if (response.StatusCode == HttpStatusCode.BadRequest &&
            (responseBody.Contains("MovieExistsValidator", StringComparison.OrdinalIgnoreCase) ||
             responseBody.Contains("already been added", StringComparison.OrdinalIgnoreCase) ||
             responseBody.Contains("This movie has already been added", StringComparison.OrdinalIgnoreCase)))
        {
            var existing = await GetMovieByTmdbIdAsync(baseUrl, apiKey, lookup.TmdbId, ct);
            return new AddMovieResult
            {
                Success = true,
                Status = "exists",
                MovieId = existing?.Id,
                Message = "Movie already exists in Radarr"
            };
        }

        return new AddMovieResult
        {
            Success = false,
            Status = "error",
            MovieId = null,
            Message = $"HTTP {(int)response.StatusCode}"
        };
    }

    public async Task<List<RootFolderResult>> GetRootFoldersAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, "rootfolder", apiKey);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"radarr rootfolder fetch failed (HTTP {(int)response.StatusCode})");

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<RootFolderResult>>(json, JsonOpts) ?? new();
    }

    public async Task<List<QualityProfileResult>> GetQualityProfilesAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, "qualityprofile", apiKey);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"radarr qualityprofile fetch failed (HTTP {(int)response.StatusCode})");

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<QualityProfileResult>>(json, JsonOpts) ?? new();
    }

    public async Task<List<TagResult>> GetTagsAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, "tag", apiKey);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"radarr tag fetch failed (HTTP {(int)response.StatusCode})");

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<TagResult>>(json, JsonOpts) ?? new();
    }

    public string BuildOpenUrl(string baseUrl, int tmdbId)
    {
        return $"{NormalizeBaseUrl(baseUrl)}/movie/{tmdbId}";
    }

    // DTOs
    private sealed class SystemStatusResponse
    {
        public string? Version { get; set; }
        public string? AppName { get; set; }
    }

    public sealed class MovieLookupResult
    {
        public int TmdbId { get; set; }
        public string Title { get; set; } = "";
        public string TitleSlug { get; set; } = "";
        public int Year { get; set; }
        public List<ImageItem>? Images { get; set; }
    }

    public sealed class MovieResult
    {
        public int Id { get; set; }
        public int TmdbId { get; set; }
        public string Title { get; set; } = "";
        public string? OriginalTitle { get; set; }
        public List<AlternateTitleItem>? AlternateTitles { get; set; }
    }

    public sealed class AlternateTitleItem
    {
        public string Title { get; set; } = "";
    }

    public sealed class ImageItem
    {
        public string CoverType { get; set; } = "";
        public string Url { get; set; } = "";
        public string RemoteUrl { get; set; } = "";
    }

    public sealed class AddMoviePayload
    {
        public string Title { get; set; } = "";
        public int TmdbId { get; set; }
        public int Year { get; set; }
        public string TitleSlug { get; set; } = "";
        public List<ImageItem>? Images { get; set; }
        public string RootFolderPath { get; set; } = "";
        public int QualityProfileId { get; set; }
        public List<int> Tags { get; set; } = new();
        public bool Monitored { get; set; } = true;
        public string MinimumAvailability { get; set; } = "released";
        public AddMovieOptions? AddOptions { get; set; }
    }

    public sealed class AddMovieOptions
    {
        public bool SearchForMovie { get; set; } = true;
    }

    public sealed class AddMovieResult
    {
        public bool Success { get; set; }
        public string Status { get; set; } = "";
        public int? MovieId { get; set; }
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
