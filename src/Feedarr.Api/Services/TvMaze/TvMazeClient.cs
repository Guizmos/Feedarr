using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services;

namespace Feedarr.Api.Services.TvMaze;

public sealed class TvMazeClient
{
    public sealed record ShowResult(
        int Id,
        string Name,
        int? PremieredYear,
        string? ImdbId,
        int? TvdbId,
        string? ImageMedium,
        string? ImageOriginal);

    private readonly HttpClient _http;
    private readonly ProviderStatsService _stats;
    private readonly ActiveExternalProviderConfigResolver _activeConfigResolver;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TvMazeClient(
        HttpClient http,
        ProviderStatsService stats,
        ActiveExternalProviderConfigResolver activeConfigResolver)
    {
        _http = http;
        _stats = stats;
        _activeConfigResolver = activeConfigResolver;
        _http.BaseAddress = new Uri("https://api.tvmaze.com/");
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<List<ShowResult>> SearchShowsAsync(string query, CancellationToken ct, int limit = 10)
    {
        if (!IsEnabled())
            return new List<ShowResult>();

        query = CleanQuery(query);
        if (string.IsNullOrWhiteSpace(query)) return new List<ShowResult>();

        var url = $"search/shows?q={Uri.EscapeDataString(query)}";
        using var resp = await GetAsyncRecorded(url, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var data = await JsonSerializer.DeserializeAsync<List<SearchItem>>(stream, JsonOpts, ct);
        if (data is null || data.Count == 0) return new List<ShowResult>();

        var results = data
            .Select(x => x.Show)
            .Where(x => x is not null && x.Id > 0 && !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => new ShowResult(
                x!.Id,
                x.Name?.Trim() ?? "",
                ExtractYear(x.Premiered),
                x.Externals?.Imdb,
                x.Externals?.Tvdb,
                x.Image?.Medium,
                x.Image?.Original))
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .DistinctBy(x => x.Id)
            .Take(Math.Clamp(limit <= 0 ? 10 : limit, 1, 50))
            .ToList();

        return results;
    }

    public async Task<ShowResult?> GetShowAsync(int id, CancellationToken ct)
    {
        if (!IsEnabled())
            return null;

        if (id <= 0) return null;
        var url = $"shows/{id}";
        using var resp = await GetAsyncRecorded(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var show = await JsonSerializer.DeserializeAsync<ShowItem>(stream, JsonOpts, ct);
        if (show is null || show.Id <= 0 || string.IsNullOrWhiteSpace(show.Name)) return null;
        return new ShowResult(
            show.Id,
            show.Name?.Trim() ?? "",
            ExtractYear(show.Premiered),
            show.Externals?.Imdb,
            show.Externals?.Tvdb,
            show.Image?.Medium,
            show.Image?.Original);
    }

    public async Task<bool> TestApiAsync(CancellationToken ct)
    {
        if (!IsEnabled())
            return false;

        var results = await SearchShowsAsync("the", ct, 1);
        return results.Count > 0;
    }

    public async Task<byte[]?> DownloadImageAsync(string url, CancellationToken ct)
    {
        if (!IsEnabled())
            return null;

        if (string.IsNullOrWhiteSpace(url)) return null;
        using var resp = await GetAsyncRecorded(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    private bool IsEnabled()
    {
        var active = _activeConfigResolver.Resolve(ExternalProviderKeys.Tvmaze);
        return active.Enabled;
    }

    private async Task<HttpResponseMessage> GetAsyncRecorded(string relativeUrl, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await _http.GetAsync(relativeUrl, ct);
            _stats.RecordTvmaze(resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);
            return resp;
        }
        catch
        {
            _stats.RecordTvmaze(false, sw.ElapsedMilliseconds);
            throw;
        }
    }

    private static string CleanQuery(string s)
    {
        s = (s ?? "").Trim();
        return s.Length > 200 ? s[..200] : s;
    }

    private static int? ExtractYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4) return null;
        return int.TryParse(date.AsSpan(0, 4), out var y) ? y : null;
    }

    private sealed class SearchItem
    {
        [JsonPropertyName("show")]
        public ShowItem? Show { get; set; }
    }

    private sealed class ShowItem
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("premiered")]
        public string? Premiered { get; set; }

        [JsonPropertyName("externals")]
        public ExternalsItem? Externals { get; set; }

        [JsonPropertyName("image")]
        public ImageItem? Image { get; set; }
    }

    private sealed class ExternalsItem
    {
        [JsonPropertyName("imdb")]
        public string? Imdb { get; set; }

        [JsonPropertyName("thetvdb")]
        public int? Tvdb { get; set; }
    }

    private sealed class ImageItem
    {
        [JsonPropertyName("medium")]
        public string? Medium { get; set; }

        [JsonPropertyName("original")]
        public string? Original { get; set; }
    }
}
