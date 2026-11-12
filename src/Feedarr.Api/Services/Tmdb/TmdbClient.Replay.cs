using System.Net;
using System.Text.Json;
using Feedarr.Api.Services.Matching.Replay;

namespace Feedarr.Api.Services.Tmdb;

public sealed partial class TmdbClient
{
    internal sealed record ReplayPage(IReadOnlyList<ObservedSearchResult> Results, int Page, int TotalPages, int TotalResults, string? Error = null, DateTimeOffset? RetryAfterUtc = null);

    // Reuse TMDB DTOs/decoding, but NOT GetJsonAsync: it writes provider statistics.
    // The caller supplies a dedicated public-provider transport with an innermost budget handler, no retries/loggers.
    internal static async Task<ReplayPage> SearchReplayPageAsync(HttpClient http, ReplayTmdbAccess access,
        string title, int? year, string type, string? language, int page, ReplayHttpBudget budget, CancellationToken ct)
    {
        ReplayPage Missing(string error) => new(Array.Empty<ObservedSearchResult>(), page, 0, 0, error);
        if (type is not ("movie" or "tv") || string.IsNullOrWhiteSpace(title) || title.Length > 200) return Missing("query_unsupported_or_too_long");
        var url = $"https://api.themoviedb.org/3/search/{type}?api_key={Uri.EscapeDataString(access.Key)}&query={Uri.EscapeDataString(title)}&include_adult=false&page={page}";
        if (year is >= 1800 and <= 2100) url += $"&{(type == "movie" ? "year" : "first_air_date_year")}={year}";
        if (language is not null) url += $"&language={Uri.EscapeDataString(language)}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Options.Set(ReplayHttpBudget.Option, budget);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var until = response.Headers.RetryAfter?.Date;
            if (until is null)
            {
                try { until = DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60)); }
                catch (ArgumentOutOfRangeException) { until = DateTimeOffset.MaxValue; }
            }
            return Missing("provider_rate_limited") with { RetryAfterUtc = until > DateTimeOffset.UtcNow ? until : DateTimeOffset.UtcNow.AddSeconds(1) };
        }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return Missing("provider_authentication_failed");
        if (!response.IsSuccessStatusCode) return Missing("provider_http_error");
        if (response.Content.Headers.ContentLength is > 1048576) return Missing("provider_response_too_large");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > 1048576) return Missing("provider_response_too_large");
            buffer.Write(chunk, 0, read);
        }
        var data = JsonSerializer.Deserialize<SearchResponse>(buffer.ToArray(), JsonOpts);
        static int? Number(JsonElement? value) => value is { ValueKind: JsonValueKind.Number } e && e.TryGetInt32(out var n) ? n : null;
        if (data?.Results is null || Number(data.Page) != page || Number(data.TotalPages) is not { } pages || pages is < 0 or > 500 ||
            Number(data.TotalResults) is not { } total || total < 0 || data.Results.Count > 20) return Missing("provider_coverage_unknown");
        if ((pages == 0 && (total != 0 || data.Results.Count != 0 || page != 1)) || (pages > 0 && page > pages))
            return Missing("provider_coverage_unknown");
        if (data.Results.Any(r => r is null || r.Id <= 0 || string.IsNullOrWhiteSpace(type == "movie" ? r.Title ?? r.Name : r.Name ?? r.Title) ||
            (r.Title?.Length ?? 0) > 512 || (r.Name?.Length ?? 0) > 512 || (r.OriginalTitle?.Length ?? 0) > 512 || (r.OriginalName?.Length ?? 0) > 512))
            return Missing("provider_response_invalid");
        var results = data.Results.Select(r => new ObservedSearchResult(new(r.Id,
            (type == "movie" ? r.Title ?? r.Name : r.Name ?? r.Title)!.Trim(),
            type == "movie" ? r.OriginalTitle ?? r.OriginalName : r.OriginalName ?? r.OriginalTitle,
            null, type, ExtractYear(type == "movie" ? r.ReleaseDate : r.FirstAirDate), r.OriginalLanguage), language)).ToArray();
        return new(results, page, pages, total);
    }
}
