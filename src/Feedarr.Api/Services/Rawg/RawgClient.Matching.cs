using System.Diagnostics;
using System.Text.Json;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Resilience;

namespace Feedarr.Api.Services.Rawg;

public sealed partial class RawgClient
{
    internal async Task<JsonElement> ReadMatchingCandidatesAsync(string title, CancellationToken ct, Action<int>? observeStatus = null)
    {
        var key = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            var error = new InvalidDataException("game_provider_unavailable");
            AnnotateMatchingFailure(error, "rawg_configuration_unavailable");
            throw error;
        }
        ct.ThrowIfCancellationRequested();
        try
        {
            var fetched = false;
            var cached = await _authentication.Responses.GetAsync(
                $"matching:{RawgAuthenticationState.Fingerprint(key)}:25:1:{title}",
                token => { fetched = true; return CreateMatchingPageAsync(title, key, token); },
                page => page.Failure is null && CompleteMatchingPage(page.Value), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            cached!.Failure?.Throw();
            Feedarr.Api.Services.Posters.MatchingAttemptDiagnostics.Current?.Response("rawg", fetched ? "provider_response" : "cache_or_coalesced");
            observeStatus?.Invoke(200);
            return cached.Value;
        }
        catch (Exception error) when (error.Data["RawgHttpStatus"] is int)
        {
            observeStatus?.Invoke((int)error.Data["RawgHttpStatus"]!);
            throw;
        }
    }

    private sealed record SearchPage(JsonElement Value,
        [property: System.Text.Json.Serialization.JsonIgnore] System.Runtime.ExceptionServices.ExceptionDispatchInfo? Failure = null);

    private async Task<SearchPage?> CreateMatchingPageAsync(string title, string key, CancellationToken ct)
    {
        try { return new(await ReadMatchingPageAsync(title, key, ct).ConfigureAwait(false)); }
        catch (OperationCanceledException error)
        {
            // The shared response cache cancels flights for OCEs. Keep the original
            // timeout metadata until the consumer rethrows it; never cache a failure.
            return new(default, System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error));
        }
    }
    private static bool CompleteMatchingPage(JsonElement value)
    {
        try { Feedarr.Api.Services.Matching.GameCandidateAdapter.Project("rawg", value); return true; }
        catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException) { return false; }
    }

    private async Task<JsonElement> ReadMatchingPageAsync(string title, string key, CancellationToken ct, int pageSize = 25)
    {
        int? status = null;
        await _authentication.Requests.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_authentication.BlockedReason(key) is { } blocked)
            {
                var error = new InvalidDataException("game_provider_unavailable");
                AnnotateMatchingFailure(error, blocked, blocked == "rawg_quota_exhausted" ? null : blocked == "rawg_http_forbidden" ? 403 : 401);
                error.Data["RawgRequestSuppressed"] = true;
                throw error;
            }
            // Keep the endpoint fixed: never follow a provider-supplied next URL.
            var url = $"https://api.rawg.io/api/games?key={Uri.EscapeDataString(key)}&search={Uri.EscapeDataString(title)}&page_size={pageSize}&page=1";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Options.Set(TransientHttpRetryHandler.DisableRetries, true);
            var watch = Stopwatch.StartNew();
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            status = (int)response.StatusCode;

            _stats.RecordExternal(ExternalProviderKeys.Rawg, response.IsSuccessStatusCode, watch.ElapsedMilliseconds);
            if (!response.IsSuccessStatusCode)
            {
                var error = new InvalidDataException("game_provider_unavailable");
                var reason = await RawgFailurePolicy.ClassifyAsync(response, ct).ConfigureAwait(false);
                _authentication.Failure(key, reason);
                AnnotateMatchingFailure(error, reason, status);
                throw error;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            _authentication.Accept(key);
            return json.RootElement.Clone();
        }
        catch (OperationCanceledException error)
        {
            // Caller cancellation is still propagated unchanged by the orchestrator.
            if (!ct.IsCancellationRequested) _authentication.Failure(key, "rawg_timeout");
            AnnotateMatchingFailure(error, "rawg_timeout", status);
            throw;
        }
        catch (HttpRequestException error)
        {
            _authentication.Failure(key, "rawg_network_error");
            AnnotateMatchingFailure(error, "rawg_network_error", error.StatusCode is { } code ? (int)code : status);
            throw;
        }
        catch (IOException error) when (!error.Data.Contains("RawgFailureReason"))
        {
            _authentication.Failure(key, "rawg_network_error");
            AnnotateMatchingFailure(error, "rawg_network_error", status);
            throw;
        }
        catch (JsonException error)
        {
            _authentication.Failure(key, "rawg_invalid_response");
            AnnotateMatchingFailure(error, "rawg_invalid_response", status);
            throw;
        }
        finally { _authentication.Requests.Release(); }
    }

    // Add only controlled metadata; preserve exception types/public outcomes and
    // never retain authenticated request URLs, response bodies or exception messages.
    internal static void AnnotateMatchingFailure(Exception error, string reason, int? status = null)
    {
        error.Data["RawgFailureReason"] = reason;
        if (status.HasValue) error.Data["RawgHttpStatus"] = status.Value;
    }
}
