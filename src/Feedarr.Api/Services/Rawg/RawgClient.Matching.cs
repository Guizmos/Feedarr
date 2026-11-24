using System.Diagnostics;
using System.Text.Json;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Resilience;

namespace Feedarr.Api.Services.Rawg;

public sealed partial class RawgClient
{
    internal async Task<JsonElement> ReadMatchingCandidatesAsync(string title, CancellationToken ct, Action<int>? observeStatus = null)
    {
        int? status = null;
        var key = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            var error = new InvalidDataException("game_provider_unavailable");
            AnnotateMatchingFailure(error, "rawg_configuration_unavailable");
            throw error;
        }
        try
        {
            // Keep the endpoint fixed: never follow a provider-supplied next URL.
            var url = $"https://api.rawg.io/api/games?key={Uri.EscapeDataString(key)}&search={Uri.EscapeDataString(title)}&page_size=25&page=1";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Options.Set(TransientHttpRetryHandler.DisableRetries, true);
            var watch = Stopwatch.StartNew();
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            status = (int)response.StatusCode;
            observeStatus?.Invoke(status.Value);
            _stats.RecordExternal(ExternalProviderKeys.Rawg, response.IsSuccessStatusCode, watch.ElapsedMilliseconds);
            if (!response.IsSuccessStatusCode)
            {
                var error = new InvalidDataException("game_provider_unavailable");
                AnnotateMatchingFailure(error, status switch {
                    401 => "rawg_http_unauthorized", 403 => "rawg_http_forbidden",
                    429 => "rawg_http_rate_limited", >= 500 => "rawg_http_server_error",
                    >= 400 => "rawg_http_client_error", _ => "rawg_http_error"
                }, status);
                throw error;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return json.RootElement.Clone();
        }
        catch (OperationCanceledException error)
        {
            // Caller cancellation is still propagated unchanged by the orchestrator.
            AnnotateMatchingFailure(error, "rawg_timeout", status);
            throw;
        }
        catch (HttpRequestException error)
        {
            AnnotateMatchingFailure(error, "rawg_network_error", error.StatusCode is { } code ? (int)code : status);
            throw;
        }
        catch (IOException error) when (!error.Data.Contains("RawgFailureReason"))
        {
            AnnotateMatchingFailure(error, "rawg_network_error", status);
            throw;
        }
        catch (JsonException error)
        {
            AnnotateMatchingFailure(error, "rawg_invalid_response", status);
            throw;
        }
    }

    // Add only controlled metadata; preserve exception types/public outcomes and
    // never retain authenticated request URLs, response bodies or exception messages.
    internal static void AnnotateMatchingFailure(Exception error, string reason, int? status = null)
    {
        error.Data["RawgFailureReason"] = reason;
        if (status.HasValue) error.Data["RawgHttpStatus"] = status.Value;
    }
}
