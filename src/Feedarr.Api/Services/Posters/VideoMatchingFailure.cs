using System.Net;
using System.Text.Json;

namespace Feedarr.Api.Services.Posters;

// Only controlled codes are persisted or logged; never exception messages or URLs.
internal sealed record VideoMatchingFailure(string Category, int Status)
{
    internal static VideoMatchingFailure Classify(Exception error, string stage) => error switch
    {
        OperationCanceledException => new("cancelled_or_timeout", 503),
        TimeoutException => new("provider_timeout", 503),
        HttpRequestException http when http.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int?)http.StatusCode >= 500 => new("provider_transient", 503),
        HttpRequestException => new("provider_permanent", 422),
        InvalidDataException data when data.Message == "tv_http_budget" => new("coverage_budget", 422),
        JsonException => new("provider_parse", 422),
        InvalidDataException => new("coverage_invalid", 422),
        _ when stage is "publication" or "materialization" => new("publication_error", 422),
        _ => new("internal_error", 422)
    };
}
