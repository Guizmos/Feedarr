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

        var snapshot = await RequestSnapshot.CreateAsync(request, ct);
        HttpResponseMessage? lastResponse = null;

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            var attemptRequest = snapshot.CreateRequest();
            try
            {
                lastResponse = await base.SendAsync(attemptRequest, ct);

                if (!IsTransientError(lastResponse) || attempt == MaxRetries)
                    return lastResponse;

                lastResponse.Dispose();
                lastResponse = null;
                attemptRequest.Dispose();
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                // Transient network error — retry
                attemptRequest.Dispose();
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxRetries)
            {
                // Per-request timeout (not global cancellation) — retry
                attemptRequest.Dispose();
            }
            catch
            {
                attemptRequest.Dispose();
                throw;
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

    private sealed class RequestSnapshot
    {
        private readonly HttpMethod _method;
        private readonly Uri? _requestUri;
        private readonly Version _version;
        private readonly HttpVersionPolicy _versionPolicy;
        private readonly List<KeyValuePair<string, IEnumerable<string>>> _headers;
        private readonly List<KeyValuePair<string, object?>> _options;
        private readonly byte[]? _contentBytes;
        private readonly List<KeyValuePair<string, IEnumerable<string>>> _contentHeaders;

        private RequestSnapshot(
            HttpMethod method,
            Uri? requestUri,
            Version version,
            HttpVersionPolicy versionPolicy,
            List<KeyValuePair<string, IEnumerable<string>>> headers,
            List<KeyValuePair<string, object?>> options,
            byte[]? contentBytes,
            List<KeyValuePair<string, IEnumerable<string>>> contentHeaders)
        {
            _method = method;
            _requestUri = requestUri;
            _version = version;
            _versionPolicy = versionPolicy;
            _headers = headers;
            _options = options;
            _contentBytes = contentBytes;
            _contentHeaders = contentHeaders;
        }

        public static async Task<RequestSnapshot> CreateAsync(HttpRequestMessage source, CancellationToken ct)
        {
            var headers = source.Headers
                .Where(h => !string.Equals(h.Key, "Host", StringComparison.OrdinalIgnoreCase))
                .Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value.ToArray()))
                .ToList();

            var options = source.Options
                .Select(o => new KeyValuePair<string, object?>(o.Key, o.Value))
                .ToList();

            byte[]? contentBytes = null;
            var contentHeaders = new List<KeyValuePair<string, IEnumerable<string>>>();
            if (source.Content is not null)
            {
                contentBytes = await source.Content.ReadAsByteArrayAsync(ct);
                contentHeaders = source.Content.Headers
                    .Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value.ToArray()))
                    .ToList();
            }

            return new RequestSnapshot(
                source.Method,
                source.RequestUri,
                source.Version,
                source.VersionPolicy,
                headers,
                options,
                contentBytes,
                contentHeaders);
        }

        public HttpRequestMessage CreateRequest()
        {
            var clone = new HttpRequestMessage(_method, _requestUri)
            {
                Version = _version,
                VersionPolicy = _versionPolicy
            };

            foreach (var option in _options)
            {
                clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
            }

            foreach (var header in _headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (_contentBytes is not null)
            {
                var content = new ByteArrayContent(_contentBytes);
                foreach (var header in _contentHeaders)
                {
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                clone.Content = content;
            }

            return clone;
        }
    }
}
