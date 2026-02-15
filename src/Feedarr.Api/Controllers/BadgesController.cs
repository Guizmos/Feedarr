using Microsoft.AspNetCore.Mvc;
using Feedarr.Api.Services;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/badges")]
public sealed class BadgesController : ControllerBase
{
    private readonly BadgeSignal _signal;

    public BadgesController(BadgeSignal signal)
    {
        _signal = signal;
    }

    private static readonly TimeSpan SseTimeout = TimeSpan.FromMinutes(30);

    [HttpGet("stream")]
    public async Task Stream(CancellationToken ct)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SseTimeout);
        var linked = cts.Token;

        await Response.WriteAsync("event: ready\ndata: ok\n\n", linked);
        await Response.Body.FlushAsync(linked);

        try
        {
            await foreach (var type in _signal.Subscribe(linked))
            {
                await Response.WriteAsync($"event: badge\ndata: {type}\n\n", linked);
                await Response.Body.FlushAsync(linked);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // SSE timeout reached — client will reconnect
        }
    }
}
