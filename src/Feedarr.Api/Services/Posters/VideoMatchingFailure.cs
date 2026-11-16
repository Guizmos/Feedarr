using System.Net;
using System.Text.Json;

namespace Feedarr.Api.Services.Posters;

// Only controlled codes are persisted or logged; never exception messages or URLs.
internal sealed record VideoMatchingFailure(string Category, int Status, string? Invariant = null)
{
    internal static VideoMatchingFailure Classify(Exception error, string stage) => error switch
    {
        OperationCanceledException => new("cancelled_or_timeout", 503),
        TimeoutException => new("provider_timeout", 503),
        HttpRequestException http when http.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int?)http.StatusCode >= 500 => new("provider_transient", 503),
        HttpRequestException => new("provider_permanent", 422),
        InvalidDataException data when data.Message == "tv_http_budget" => new("coverage_budget", 422, "tv_http_budget"),
        JsonException => new("provider_parse", 422),
        InvalidDataException data when KnownInvariant(data.Message) => new("coverage_invalid", 422, data.Message),
        InvalidDataException => new("coverage_invalid", 422),
        _ when stage is "publication" or "materialization" => new("publication_error", 422),
        _ => new("internal_error", 422)
    };
    // Exact allowlist: never accept arbitrary exception text, even with a trusted prefix.
    internal static bool KnownInvariant(string code) => code is
        "tmdb_details_unavailable" or
        "tmdb_poster_unavailable" or
        "tmdb_tv_external_identity_unavailable" or
        "tv_candidate_budget" or
        "tv_conflicting_provider_records" or
        "tv_details_identity_unavailable" or
        "tv_episode_date_conflict" or
        "tv_episode_identity_conflict" or
        "tv_evidence_budget" or
        "tv_external_identity_conflict" or
        "tv_http_budget" or
        "tv_multiple_canonical_identities" or
        "tv_provider_year_conflict" or
        "tv_response_budget" or
        "tvmaze_poster_unavailable" or
        "tvmaze_publication_identity_unavailable" or
        "tvmaze_publication_title_unavailable" or
        "tvmaze_search_coverage_unavailable" or
        "tvmaze_search_invalid";
}
