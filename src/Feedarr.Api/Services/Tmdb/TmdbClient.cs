using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Services;

namespace Feedarr.Api.Services.Tmdb;

public sealed class TmdbClient
{
    public sealed record SearchResult(int TmdbId, string Title, string? OriginalTitle, string? PosterPath, string MediaType, int? Year, string? OriginalLanguage);
    public sealed record DetailsResult(
        string? Title,
        string? Overview,
        string? Tagline,
        string? Genres,
        string? ReleaseDate,
        int? RuntimeMinutes,
        double? Rating,
        int? Votes,
        string? Directors,
        string? Writers,
        string? Cast);

    private readonly HttpClient _http;
    private readonly SettingsRepository _settings;
    private readonly ProviderStatsService _stats;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TmdbClient(HttpClient http, SettingsRepository settings, ProviderStatsService stats)
    {
        _http = http;
        _settings = settings;
        _stats = stats;

        _http.BaseAddress = new Uri("https://api.themoviedb.org/3/");
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    private string? TryGetApiKey()
    {
        var ext = _settings.GetExternal(new ExternalSettings());
        if (ext.TmdbEnabled == false) return null;
        var key = (ext.TmdbApiKey ?? "").Trim();
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    private string GetApiKeyOrThrow()
    {
        var key = TryGetApiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("TMDB api key missing or disabled (api/settings/external)");
        return key;
    }

    private static string CleanQuery(string s)
    {
        s = (s ?? "").Trim();
        return s.Length > 200 ? s[..200] : s;
    }

    private async Task<T?> GetJsonAsync<T>(string relativeUrl, CancellationToken ct)
    {
        var recorded = false;
        var sw = Stopwatch.StartNew();

        try
        {
            using var resp = await _http.GetAsync(relativeUrl, ct);
            var ok = resp.IsSuccessStatusCode;
            _stats.RecordTmdb(ok, sw.ElapsedMilliseconds);
            recorded = true;
            if (!ok) resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOpts, ct);
        }
        catch
        {
            if (!recorded)
                _stats.RecordTmdb(false, sw.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<bool> TestApiKeyAsync(CancellationToken ct)
    {
        var key = GetApiKeyOrThrow();
        var url = $"configuration?api_key={Uri.EscapeDataString(key)}";
        var recorded = false;
        var sw = Stopwatch.StartNew();

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            var ok = resp.IsSuccessStatusCode;
            _stats.RecordTmdb(ok, sw.ElapsedMilliseconds);
            recorded = true;
            return ok;
        }
        catch
        {
            if (!recorded)
                _stats.RecordTmdb(false, sw.ElapsedMilliseconds);
            throw;
        }
    }

    // --------- Search Movie ----------
    public async Task<(int tmdbId, string? posterPath)?> SearchMovieAsync(string title, int? year, CancellationToken ct, bool requirePoster = true)
    {
        var key = TryGetApiKey();
        if (string.IsNullOrWhiteSpace(key)) return null;
        title = CleanQuery(title);
        if (string.IsNullOrWhiteSpace(title)) return null;

        var url = $"search/movie?api_key={Uri.EscapeDataString(key)}&query={Uri.EscapeDataString(title)}&include_adult=false";
        if (year is >= 1800 and <= 2100) url += $"&year={year.Value}";

        var r = await GetJsonAsync<SearchResponse>(url, ct);

        // on prend le 1er qui a un poster (ou le 1er tout court si poster non requis)
        var best = requirePoster
            ? r?.Results?.FirstOrDefault(x => x.Id > 0 && !string.IsNullOrWhiteSpace(x.PosterPath))
            : r?.Results?.FirstOrDefault(x => x.Id > 0);
        if (best is not null) return (best.Id, best.PosterPath);

        // fallback : sans year (parfois year foire)
        if (year is not null)
        {
            var url2 = $"search/movie?api_key={Uri.EscapeDataString(key)}&query={Uri.EscapeDataString(title)}&include_adult=false";
            var r2 = await GetJsonAsync<SearchResponse>(url2, ct);
            var best2 = requirePoster
                ? r2?.Results?.FirstOrDefault(x => x.Id > 0 && !string.IsNullOrWhiteSpace(x.PosterPath))
                : r2?.Results?.FirstOrDefault(x => x.Id > 0);
            if (best2 is not null) return (best2.Id, best2.PosterPath);
        }

        return null;
    }

    public async Task<List<SearchResult>> SearchMovieListAsync(string title, int? year, CancellationToken ct, int limit = 10)
    {
        var results = new List<SearchResult>();
        results.AddRange(await SearchMovieListWithLanguageAsync(title, year, null, ct, limit));
        results.AddRange(await SearchMovieListWithLanguageAsync(title, year, "fr-FR", ct, limit));
        return results;
    }

    // --------- Search TV ----------
    public async Task<(int tmdbId, string? posterPath)?> SearchTvAsync(string title, int? year, CancellationToken ct, bool requirePoster = true)
    {
        var key = TryGetApiKey();
        if (string.IsNullOrWhiteSpace(key)) return null;
        title = CleanQuery(title);
        if (string.IsNullOrWhiteSpace(title)) return null;

        var url = $"search/tv?api_key={Uri.EscapeDataString(key)}&query={Uri.EscapeDataString(title)}&include_adult=false";
        if (year is >= 1800 and <= 2100) url += $"&first_air_date_year={year.Value}";

        var r = await GetJsonAsync<SearchResponse>(url, ct);

        var best = requirePoster
            ? r?.Results?.FirstOrDefault(x => x.Id > 0 && !string.IsNullOrWhiteSpace(x.PosterPath))
            : r?.Results?.FirstOrDefault(x => x.Id > 0);
        if (best is not null) return (best.Id, best.PosterPath);

        // fallback : sans year
        if (year is not null)
        {
            var url2 = $"search/tv?api_key={Uri.EscapeDataString(key)}&query={Uri.EscapeDataString(title)}&include_adult=false";
            var r2 = await GetJsonAsync<SearchResponse>(url2, ct);
            var best2 = requirePoster
                ? r2?.Results?.FirstOrDefault(x => x.Id > 0 && !string.IsNullOrWhiteSpace(x.PosterPath))
                : r2?.Results?.FirstOrDefault(x => x.Id > 0);
            if (best2 is not null) return (best2.Id, best2.PosterPath);
        }

        return null;
    }

    public async Task<List<SearchResult>> SearchTvListAsync(string title, int? year, CancellationToken ct, int limit = 10)
    {
        var results = new List<SearchResult>();
        results.AddRange(await SearchTvListWithLanguageAsync(title, year, null, ct, limit));
        results.AddRange(await SearchTvListWithLanguageAsync(title, year, "fr-FR", ct, limit));
        return results;
    }

    // --------- Download poster ----------
    public async Task<byte[]?> DownloadPosterW500Async(string posterPath, CancellationToken ct)
    {
        var key = TryGetApiKey();
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (string.IsNullOrWhiteSpace(posterPath)) return null;

        var url = $"https://image.tmdb.org/t/p/w500{posterPath}";
        var recorded = false;
        var sw = Stopwatch.StartNew();

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            var ok = resp.IsSuccessStatusCode;
            _stats.RecordTmdb(ok, sw.ElapsedMilliseconds);
            recorded = true;
            if (!ok) resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch
        {
            if (!recorded)
                _stats.RecordTmdb(false, sw.ElapsedMilliseconds);
            throw;
        }
    }

    /// <summary>
    /// Picks a poster path with language priority: fr, en, es, it, null.
    /// Returns null when no poster is available for the title.
    /// </summary>
    public async Task<string?> GetPreferredPosterPathAsync(int tmdbId, string mediaType, CancellationToken ct)
    {
        var key = TryGetApiKey();
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (tmdbId <= 0) return null;

        var kind = mediaType == "series" ? "tv" : "movie";
        var url = $"{kind}/{tmdbId}/images?api_key={Uri.EscapeDataString(key)}&include_image_language=fr,en,es,it,null";
        var data = await GetJsonAsync<ImagesResponse>(url, ct);
        var posters = data?.Posters?
            .Where(x => !string.IsNullOrWhiteSpace(x.FilePath))
            .OrderByDescending(x => x.VoteCount)
            .ThenByDescending(x => x.VoteAverage)
            .ThenByDescending(x => x.Width * x.Height)
            .ToList();
        if (posters is null || posters.Count == 0) return null;

        static bool IsLang(ImageItem item, string lang)
            => string.Equals((item.Iso639_1 ?? "").Trim(), lang, StringComparison.OrdinalIgnoreCase);

        static bool IsNoLang(ImageItem item)
            => string.IsNullOrWhiteSpace(item.Iso639_1) || string.Equals(item.Iso639_1, "null", StringComparison.OrdinalIgnoreCase);

        var fr = posters.FirstOrDefault(x => IsLang(x, "fr"));
        if (!string.IsNullOrWhiteSpace(fr?.FilePath)) return fr.FilePath;

        var en = posters.FirstOrDefault(x => IsLang(x, "en"));
        if (!string.IsNullOrWhiteSpace(en?.FilePath)) return en.FilePath;

        var es = posters.FirstOrDefault(x => IsLang(x, "es"));
        if (!string.IsNullOrWhiteSpace(es?.FilePath)) return es.FilePath;

        var it = posters.FirstOrDefault(x => IsLang(x, "it"));
        if (!string.IsNullOrWhiteSpace(it?.FilePath)) return it.FilePath;

        var noLang = posters.FirstOrDefault(IsNoLang);
        if (!string.IsNullOrWhiteSpace(noLang?.FilePath)) return noLang.FilePath;

        return posters.FirstOrDefault()?.FilePath;
    }

    // --------- Backdrops (banner) ----------
    public async Task<string?> GetBackdropPathAsync(int tmdbId, string mediaType, CancellationToken ct)
    {
        var key = TryGetApiKey();
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (tmdbId <= 0) return null;

        var kind = mediaType == "series" ? "tv" : "movie";
        var url = $"{kind}/{tmdbId}/images?api_key={Uri.EscapeDataString(key)}&include_image_language=en,null";
        var data = await GetJsonAsync<ImagesResponse>(url, ct);
        var path = data?.Backdrops?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.FilePath))?.FilePath;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    public async Task<byte[]?> DownloadBackdropW780Async(string backdropPath, CancellationToken ct)
    {
        var key = TryGetApiKey();
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (string.IsNullOrWhiteSpace(backdropPath)) return null;

        var url = $"https://image.tmdb.org/t/p/w780{backdropPath}";
        var recorded = false;
        var sw = Stopwatch.StartNew();

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            var ok = resp.IsSuccessStatusCode;
            _stats.RecordTmdb(ok, sw.ElapsedMilliseconds);
            recorded = true;
            if (!ok) resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch
        {
            if (!recorded)
                _stats.RecordTmdb(false, sw.ElapsedMilliseconds);
            throw;
        }
    }

    // --------- External IDs (TV) ----------
    public async Task<int?> GetTvdbIdAsync(int tmdbId, CancellationToken ct)
    {
        var key = TryGetApiKey();
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (tmdbId <= 0) return null;

        var url = $"tv/{tmdbId}/external_ids?api_key={Uri.EscapeDataString(key)}";
        try
        {
            var r = await GetJsonAsync<ExternalIdsResponse>(url, ct);
            return r?.TvdbId > 0 ? r.TvdbId : null;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<int?> GetTvTmdbIdByTvdbIdAsync(int tvdbId, CancellationToken ct)
    {
        var key = TryGetApiKey();
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (tvdbId <= 0) return null;

        var url = $"find/{tvdbId}?api_key={Uri.EscapeDataString(key)}&external_source=tvdb_id";
        try
        {
            var r = await GetJsonAsync<FindResponse>(url, ct);
            var tmdbId = r?.TvResults?.FirstOrDefault(x => x.Id > 0)?.Id;
            return tmdbId > 0 ? tmdbId : null;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // --------- Details (Overview/Genres/etc) ----------
    public async Task<DetailsResult?> GetMovieDetailsAsync(int tmdbId, CancellationToken ct)
    {
        var key = TryGetApiKey();
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (tmdbId <= 0) return null;

        var url = $"movie/{tmdbId}?api_key={Uri.EscapeDataString(key)}&language=fr-FR";
        var r = await GetJsonAsync<MovieDetailsResponse>(url, ct);
        if (r is null) return null;
        var credits = await GetMovieCreditsAsync(tmdbId, ct);

        return new DetailsResult(
            r.Title?.Trim(),
            r.Overview?.Trim(),
            r.Tagline?.Trim(),
            JoinGenres(r.Genres),
            r.ReleaseDate?.Trim(),
            r.Runtime > 0 ? r.Runtime : null,
            r.VoteAverage > 0 ? r.VoteAverage : null,
            r.VoteCount > 0 ? r.VoteCount : null,
            credits?.Directors,
            credits?.Writers,
            credits?.Cast
        );
    }

    public async Task<DetailsResult?> GetTvDetailsAsync(int tmdbId, CancellationToken ct)
    {
        var key = TryGetApiKey();
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (tmdbId <= 0) return null;

        var url = $"tv/{tmdbId}?api_key={Uri.EscapeDataString(key)}&language=fr-FR";
        var r = await GetJsonAsync<TvDetailsResponse>(url, ct);
        if (r is null) return null;
        var credits = await GetTvCreditsAsync(tmdbId, ct);

        var runtime = r.EpisodeRunTime?.FirstOrDefault(x => x > 0);
        return new DetailsResult(
            r.Name?.Trim(),
            r.Overview?.Trim(),
            r.Tagline?.Trim(),
            JoinGenres(r.Genres),
            r.FirstAirDate?.Trim(),
            runtime > 0 ? runtime : null,
            r.VoteAverage > 0 ? r.VoteAverage : null,
            r.VoteCount > 0 ? r.VoteCount : null,
            credits?.Directors,
            credits?.Writers,
            credits?.Cast
        );
    }

    // DTOs TMDB (snake_case)
    private sealed class SearchResponse
    {
        [JsonPropertyName("results")]
        public List<SearchItem>? Results { get; set; }
    }

    private sealed class SearchItem
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("poster_path")]
        public string? PosterPath { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; } // movie

        [JsonPropertyName("original_title")]
        public string? OriginalTitle { get; set; } // movie

        [JsonPropertyName("name")]
        public string? Name { get; set; } // tv

        [JsonPropertyName("original_name")]
        public string? OriginalName { get; set; } // tv

        [JsonPropertyName("original_language")]
        public string? OriginalLanguage { get; set; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("first_air_date")]
        public string? FirstAirDate { get; set; }
    }

    private sealed class ExternalIdsResponse
    {
        [JsonPropertyName("tvdb_id")]
        public int? TvdbId { get; set; }
    }

    private sealed class FindResponse
    {
        [JsonPropertyName("tv_results")]
        public List<FindItem>? TvResults { get; set; }
    }

    private sealed class FindItem
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
    }

    private sealed class ImagesResponse
    {
        [JsonPropertyName("backdrops")]
        public List<ImageItem>? Backdrops { get; set; }

        [JsonPropertyName("posters")]
        public List<ImageItem>? Posters { get; set; }
    }

    private sealed class ImageItem
    {
        [JsonPropertyName("file_path")]
        public string? FilePath { get; set; }

        [JsonPropertyName("iso_639_1")]
        public string? Iso639_1 { get; set; }

        [JsonPropertyName("vote_average")]
        public double VoteAverage { get; set; }

        [JsonPropertyName("vote_count")]
        public int VoteCount { get; set; }

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }
    }

    private sealed class MovieDetailsResponse
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("overview")]
        public string? Overview { get; set; }

        [JsonPropertyName("tagline")]
        public string? Tagline { get; set; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("runtime")]
        public int Runtime { get; set; }

        [JsonPropertyName("vote_average")]
        public double VoteAverage { get; set; }

        [JsonPropertyName("vote_count")]
        public int VoteCount { get; set; }

        [JsonPropertyName("genres")]
        public List<GenreItem>? Genres { get; set; }
    }

    private sealed class TvDetailsResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("overview")]
        public string? Overview { get; set; }

        [JsonPropertyName("tagline")]
        public string? Tagline { get; set; }

        [JsonPropertyName("first_air_date")]
        public string? FirstAirDate { get; set; }

        [JsonPropertyName("episode_run_time")]
        public List<int>? EpisodeRunTime { get; set; }

        [JsonPropertyName("vote_average")]
        public double VoteAverage { get; set; }

        [JsonPropertyName("vote_count")]
        public int VoteCount { get; set; }

        [JsonPropertyName("genres")]
        public List<GenreItem>? Genres { get; set; }
    }

    private sealed class GenreItem
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed record CreditsResult(string? Directors, string? Writers, string? Cast);

    private sealed class CreditsResponse
    {
        [JsonPropertyName("cast")]
        public List<CastItem>? Cast { get; set; }

        [JsonPropertyName("crew")]
        public List<CrewItem>? Crew { get; set; }
    }

    private sealed class CastItem
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("order")]
        public int Order { get; set; }
    }

    private sealed class CrewItem
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("job")]
        public string? Job { get; set; }
    }

    private static int? ExtractYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return null;
        if (date.Length < 4) return null;
        return int.TryParse(date.AsSpan(0, 4), out var y) ? y : null;
    }

    private static string? JoinGenres(List<GenreItem>? items)
    {
        if (items is null || items.Count == 0) return null;
        var names = items
            .Select(x => x.Name?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        return names.Count == 0 ? null : string.Join(", ", names);
    }

    private async Task<CreditsResult?> GetMovieCreditsAsync(int tmdbId, CancellationToken ct)
    {
        var key = TryGetApiKey();
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (tmdbId <= 0) return null;

        var url = $"movie/{tmdbId}/credits?api_key={Uri.EscapeDataString(key)}&language=fr-FR";
        var r = await GetJsonAsync<CreditsResponse>(url, ct);
        if (r is null) return null;

        var directors = r.Crew?
            .Where(x => string.Equals(x.Job, "Director", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Name?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .Take(3)
            .ToList();

        var writers = r.Crew?
            .Where(x => string.Equals(x.Job, "Writer", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(x.Job, "Screenplay", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Name?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .Take(3)
            .ToList();

        var cast = r.Cast?
            .OrderBy(x => x.Order)
            .Select(x => x.Name?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .Take(4)
            .ToList();

        return new CreditsResult(
            directors is null || directors.Count == 0 ? null : string.Join(", ", directors),
            writers is null || writers.Count == 0 ? null : string.Join(", ", writers),
            cast is null || cast.Count == 0 ? null : string.Join(", ", cast)
        );
    }

    private async Task<CreditsResult?> GetTvCreditsAsync(int tmdbId, CancellationToken ct)
    {
        var key = TryGetApiKey();
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (tmdbId <= 0) return null;

        var url = $"tv/{tmdbId}/credits?api_key={Uri.EscapeDataString(key)}&language=fr-FR";
        var r = await GetJsonAsync<CreditsResponse>(url, ct);
        if (r is null) return null;

        var directors = r.Crew?
            .Where(x => string.Equals(x.Job, "Creator", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(x.Job, "Executive Producer", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Name?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .Take(3)
            .ToList();

        var writers = r.Crew?
            .Where(x => string.Equals(x.Job, "Writer", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Name?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .Take(3)
            .ToList();

        var cast = r.Cast?
            .OrderBy(x => x.Order)
            .Select(x => x.Name?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .Take(4)
            .ToList();

        return new CreditsResult(
            directors is null || directors.Count == 0 ? null : string.Join(", ", directors),
            writers is null || writers.Count == 0 ? null : string.Join(", ", writers),
            cast is null || cast.Count == 0 ? null : string.Join(", ", cast)
        );
    }

    private async Task<List<SearchResult>> SearchMovieListWithLanguageAsync(
        string title,
        int? year,
        string? language,
        CancellationToken ct,
        int limit)
    {
        var key = TryGetApiKey();
        if (string.IsNullOrWhiteSpace(key)) return new List<SearchResult>();
        title = CleanQuery(title);
        if (string.IsNullOrWhiteSpace(title)) return new List<SearchResult>();

        var url = $"search/movie?api_key={Uri.EscapeDataString(key)}&query={Uri.EscapeDataString(title)}&include_adult=false";
        if (!string.IsNullOrWhiteSpace(language))
            url += $"&language={Uri.EscapeDataString(language)}";
        if (year is >= 1800 and <= 2100) url += $"&year={year.Value}";

        var r = await GetJsonAsync<SearchResponse>(url, ct);
        if (r?.Results is null) return new List<SearchResult>();

        return r.Results
            .Where(x => x.Id > 0)
            .Select(x => new SearchResult(
                x.Id,
                (x.Title ?? x.Name ?? "").Trim(),
                (x.OriginalTitle ?? x.OriginalName ?? "").Trim(),
                x.PosterPath,
                "movie",
                ExtractYear(x.ReleaseDate),
                x.OriginalLanguage
            ))
            .Where(x => !string.IsNullOrWhiteSpace(x.Title))
            .Take(Math.Clamp(limit <= 0 ? 10 : limit, 1, 50))
            .ToList();
    }

    private async Task<List<SearchResult>> SearchTvListWithLanguageAsync(
        string title,
        int? year,
        string? language,
        CancellationToken ct,
        int limit)
    {
        var key = TryGetApiKey();
        if (string.IsNullOrWhiteSpace(key)) return new List<SearchResult>();
        title = CleanQuery(title);
        if (string.IsNullOrWhiteSpace(title)) return new List<SearchResult>();

        var url = $"search/tv?api_key={Uri.EscapeDataString(key)}&query={Uri.EscapeDataString(title)}&include_adult=false";
        if (!string.IsNullOrWhiteSpace(language))
            url += $"&language={Uri.EscapeDataString(language)}";
        if (year is >= 1800 and <= 2100) url += $"&first_air_date_year={year.Value}";

        var r = await GetJsonAsync<SearchResponse>(url, ct);
        if (r?.Results is null) return new List<SearchResult>();

        return r.Results
            .Where(x => x.Id > 0)
            .Select(x => new SearchResult(
                x.Id,
                (x.Name ?? x.Title ?? "").Trim(),
                (x.OriginalName ?? x.OriginalTitle ?? "").Trim(),
                x.PosterPath,
                "series",
                ExtractYear(x.FirstAirDate),
                x.OriginalLanguage
            ))
            .Where(x => !string.IsNullOrWhiteSpace(x.Title))
            .Take(Math.Clamp(limit <= 0 ? 10 : limit, 1, 50))
            .ToList();
    }
}
