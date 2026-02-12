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

    [HttpGet("stream")]
    public async Task Stream(CancellationToken ct)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        await Response.WriteAsync("event: ready\ndata: ok\n\n", ct);
        await Response.Body.FlushAsync(ct);

        await foreach (var type in _signal.Subscribe(ct))
        {
            await Response.WriteAsync($"event: badge\ndata: {type}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }
}
