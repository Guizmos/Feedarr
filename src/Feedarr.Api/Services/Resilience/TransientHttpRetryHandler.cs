using System.Net;

namespace Feedarr.Api.Services.Resilience;

/// <summary>
/// DelegatingHandler that retries transient HTTP errors (5xx, 408, 429)
/// with exponential backoff + jitter. Only retries idempotent methods (GET/HEAD).
/// </summary>
internal sealed class TransientHttpRetryHandler : DelegatingHandler
{
    private const int MaxRetries = 2;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        // Only retry idempotent methods — POST/PUT may not be safe to replay
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            return await base.SendAsync(request, ct);

        HttpResponseMessage? lastResponse = null;

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                lastResponse = await base.SendAsync(request, ct);

                if (!IsTransientError(lastResponse) || attempt == MaxRetries)
                    return lastResponse;

                lastResponse.Dispose();
                lastResponse = null;
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                // Transient network error — retry
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxRetries)
            {
                // Per-request timeout (not global cancellation) — retry
            }

            var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt))
                          + TimeSpan.FromMilliseconds(Random.Shared.Next(100, 500));
            await Task.Delay(backoff, ct);
        }

        // Should not be reached, but satisfy the compiler
        return lastResponse ?? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
    }

    private static bool IsTransientError(HttpResponseMessage response)
    {
        return (int)response.StatusCode >= 500
               || response.StatusCode is HttpStatusCode.RequestTimeout
                                      or HttpStatusCode.TooManyRequests;
    }
}
