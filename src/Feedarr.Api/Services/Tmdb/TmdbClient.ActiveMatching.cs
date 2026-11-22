using System.Text.Json;

namespace Feedarr.Api.Services.Tmdb;

public sealed partial class TmdbClient
{
    internal sealed record VideoMatchingPage(IReadOnlyList<SearchResult> Results, int TotalPages, int TotalResults);

    internal IReadOnlyList<string> VideoMatchingLanguages => new[] { GetMediaInfoLanguage(), "en-US" }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal async Task<int?> ResolveRssExternalIdAsync(string provider, string id, string type, CancellationToken ct)
    {
        if (type is not ("movie" or "tv")) return null;
        var source = provider switch
        {
            "imdb" when System.Text.RegularExpressions.Regex.IsMatch(id, @"^tt\d{7,10}$") && id[2..].Any(c => c != '0') => "imdb_id",
            "tvdb" when type == "tv" && int.TryParse(id, out var number) && number > 0 => "tvdb_id",
            _ => null
        };
        if (source is null) return null;
        var value = await GetJsonAsync<JsonElement>($"find/{Uri.EscapeDataString(id)}?api_key={Uri.EscapeDataString(GetApiKeyOrThrow())}&external_source={source}", ct, disableRetries: true).ConfigureAwait(false);
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(type == "movie" ? "movie_results" : "tv_results", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return null;
        var ids = new List<int>();
        foreach (var row in rows.EnumerateArray())
        {
            if (Feedarr.Api.Services.Matching.TelevisionCandidates.Number(row, "id") is not > 0) return null;
            if (row.TryGetProperty("media_type", out var media) && (media.ValueKind != JsonValueKind.String || media.GetString() != type)) return null;
            ids.Add(row.GetProperty("id").GetInt32());
        }
        var unique = ids.Distinct().ToArray();
        return unique.Length == 1 ? unique[0] : null;
    }

    internal async Task<JsonElement?> GetMovieMatchingTitlesAsync(string id, CancellationToken ct)
    {
        if (!int.TryParse(id, out var number) || number <= 0) throw new ArgumentException(nameof(id));
        var path = $"movie/{number}/alternative_titles?api_key={Uri.EscapeDataString(GetApiKeyOrThrow())}";
        return await GetJsonAsync<JsonElement>(path, ct, disableRetries: true).ConfigureAwait(false);
    }

    // Full pages only: historical list helpers deliberately truncate and cannot prove coverage.
    internal async Task<VideoMatchingPage?> SearchVideoMatchingPageAsync(string title, string language, int page, string type, CancellationToken ct)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(GetApiKeyOrThrow())));
        return await _matchingResponses.GetAsync($"search:{key}:{type}:{language}:{page}:{title}",
            token => SearchVideoMatchingPageCoreAsync(title, language, page, type, token), _ => true, ct).ConfigureAwait(false);
    }

    private async Task<VideoMatchingPage?> SearchVideoMatchingPageCoreAsync(string title, string language, int page, string type, CancellationToken ct)
    {
        if (type is not ("movie" or "tv")) throw new ArgumentException(nameof(type));
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200) return null;
        var key = GetApiKeyOrThrow();
        // No year filter: hypothetical years must not hide competitors; parsed-year tolerance stays in the ranker.
        var url = $"search/{type}?api_key={Uri.EscapeDataString(key)}&query={Uri.EscapeDataString(title)}&language={Uri.EscapeDataString(language)}&include_adult=false&page={page}";
        var data = await GetJsonAsync<SearchResponse>(url, ct, disableRetries: true).ConfigureAwait(false);
        static int? Number(JsonElement? element) => element is { ValueKind: JsonValueKind.Number } e && e.TryGetInt32(out var n) ? n : null;
        if (data?.Results is null || Number(data.Page) != page || Number(data.TotalPages) is not { } pages || pages is < 0 or > 500 ||
            Number(data.TotalResults) is not { } total || total < 0 || data.Results.Count > 20) return null;
        if ((total == 0 && (pages is not (0 or 1) || page != 1 || data.Results.Count != 0)) ||
            (total > 0 && (pages != (int)Math.Ceiling(total / 20d) || page > pages ||
             data.Results.Count != Math.Min(20, total - (page - 1) * 20)))) return null;
        if (data.Results.Any(r => r is null || r.Id <= 0 || (r.MediaType is { } media && (media.ValueKind != JsonValueKind.String || media.GetString() != type)) || string.IsNullOrWhiteSpace(type == "movie" ? r.Title : r.Name) ||
            (r.Title?.Length ?? 0) > 512 || (r.Name?.Length ?? 0) > 512 || (r.OriginalTitle?.Length ?? 0) > 512 || (r.OriginalName?.Length ?? 0) > 512)) return null;
        return new(data.Results.Select(r => new SearchResult(r.Id, (type == "movie" ? r.Title : r.Name)!, type == "movie" ? r.OriginalTitle : r.OriginalName, r.PosterPath,
            type, ExtractYear(type == "movie" ? r.ReleaseDate : r.FirstAirDate), r.OriginalLanguage, r.Overview)).ToArray(), pages, total);
    }
}
