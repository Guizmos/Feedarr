using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Services.DirectRss;

public sealed class DirectRssClient(HttpClient http, DirectRssParser parser)
{
    private const int MaxBytes = 5_000_000;

    public async Task<List<DirectRssEntry>> FetchAsync(string url, long feedId, string categoryKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OutboundUrlGuard.TryNormalizeTorznabUrl(url, out _, out _))
            throw new ArgumentException("Invalid RSS URL");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var requestCt = timeout.Token;
        try
        {
            // Preserve the configured URI verbatim: no Torznab parameters, pagination or authentication changes.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/rss+xml, application/xml, text/xml");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCt).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new DirectRssException($"RSS HTTP {(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength > MaxBytes) throw new DirectRssException("RSS response too large");
            await using var stream = await response.Content.ReadAsStreamAsync(requestCt).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, requestCt).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > MaxBytes) throw new DirectRssException("RSS response too large");
                buffer.Write(chunk, 0, count);
            }
            requestCt.ThrowIfCancellationRequested();
            buffer.Position = 0;
            return parser.Parse(buffer, feedId, categoryKey);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (DirectRssException) { throw; }
        // No inner exception: signed URLs and server payloads must not reach API/logs.
        catch (Exception) { throw new DirectRssException("RSS request or XML parsing failed"); }
    }
}

public sealed class DirectRssException(string message) : Exception(message);
