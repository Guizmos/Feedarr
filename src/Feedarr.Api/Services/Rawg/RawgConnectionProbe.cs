using System.Diagnostics;
using Feedarr.Api.Services.Resilience;
using Feedarr.Api.Services.ExternalProviders;

namespace Feedarr.Api.Services.Rawg;

internal static class RawgConnectionProbe
{
    internal static async Task<(bool Ok, string? Error)> TestAsync(HttpClient http, Uri url, string key,
        RawgAuthenticationState state, ProviderStatsService stats, CancellationToken ct)
    {
        await state.Requests.WaitAsync(ct).ConfigureAwait(false);
        var recorded = false; var watch = Stopwatch.StartNew();
        try
        {
            if (!state.TryStartProbe(key)) return (false, "rawg_probe_cooldown");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Options.Set(TransientHttpRetryHandler.DisableRetries, true);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            stats.RecordExternal(ExternalProviderKeys.Rawg, response.IsSuccessStatusCode, watch.ElapsedMilliseconds);
            recorded = true;
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var body = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                    var root = body.RootElement;
                    if (root.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        root.TryGetProperty("results", out var rows) && rows.ValueKind == System.Text.Json.JsonValueKind.Array &&
                        root.TryGetProperty("count", out var count) && count.ValueKind == System.Text.Json.JsonValueKind.Number && count.TryGetInt32(out var total) && total >= rows.GetArrayLength())
                    { state.Accept(key); return (true, null); }
                }
                catch (System.Text.Json.JsonException) { }
                state.Failure(key, "rawg_invalid_response");
                return (false, "rawg_invalid_response");
            }
            var reason = await RawgFailurePolicy.ClassifyAsync(response, ct).ConfigureAwait(false);
            state.Failure(key, reason);
            return (false, reason);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            if (!recorded) stats.RecordExternal(ExternalProviderKeys.Rawg, false, watch.ElapsedMilliseconds);
            state.Failure(key, error is OperationCanceledException ? "rawg_timeout" : "rawg_network_error");
            throw;
        }
        finally { state.Requests.Release(); }
    }
}
