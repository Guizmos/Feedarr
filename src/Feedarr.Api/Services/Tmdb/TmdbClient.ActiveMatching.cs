using System.Text.Json;

namespace Feedarr.Api.Services.Tmdb;

public sealed partial class TmdbClient
{
    internal sealed record MovieMatchingPage(IReadOnlyList<SearchResult> Results, int TotalPages, int TotalResults);

    internal IReadOnlyList<string> MovieMatchingLanguages => new[] { GetMediaInfoLanguage(), "en-US" }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    // Full pages only: historical list helpers deliberately truncate and cannot prove coverage.
    internal async Task<MovieMatchingPage?> SearchMovieMatchingPageAsync(string title, string language, int page, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200) return null;
        var key = GetApiKeyOrThrow();
        // No year filter: hypothetical years must not hide competitors; parsed-year tolerance stays in the ranker.
        var url = $"search/movie?api_key={Uri.EscapeDataString(key)}&query={Uri.EscapeDataString(title)}&language={Uri.EscapeDataString(language)}&include_adult=false&page={page}";
        var data = await GetJsonAsync<SearchResponse>(url, ct, disableRetries: true).ConfigureAwait(false);
        static int? Number(JsonElement? element) => element is { ValueKind: JsonValueKind.Number } e && e.TryGetInt32(out var n) ? n : null;
        if (data?.Results is null || Number(data.Page) != page || Number(data.TotalPages) is not { } pages || pages is < 0 or > 500 ||
            Number(data.TotalResults) is not { } total || total < 0 || data.Results.Count > 20) return null;
        if ((total == 0 && (pages != 0 || page != 1 || data.Results.Count != 0)) ||
            (total > 0 && (pages != (int)Math.Ceiling(total / 20d) || page > pages ||
             data.Results.Count != Math.Min(20, total - (page - 1) * 20)))) return null;
        if (data.Results.Any(r => r is null || r.Id <= 0 || (r.MediaType is { } media && (media.ValueKind != JsonValueKind.String || media.GetString() != "movie")) || string.IsNullOrWhiteSpace(r.Title) ||
            r.Title.Length > 512 || (r.OriginalTitle?.Length ?? 0) > 512)) return null;
        return new(data.Results.Select(r => new SearchResult(r.Id, r.Title!, r.OriginalTitle, r.PosterPath,
            "movie", ExtractYear(r.ReleaseDate), r.OriginalLanguage)).ToArray(), pages, total);
    }
}
