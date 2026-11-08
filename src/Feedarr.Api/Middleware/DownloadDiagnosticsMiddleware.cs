using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Diagnostics;

namespace Feedarr.Api.Middleware;

public sealed class DownloadDiagnosticsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, DownloadDiagnostics diagnostics, DownloadBrowserSession sessions)
    {
        var parts = context.Request.Path.Value?.Split('/');
        if (!HttpMethods.IsGet(context.Request.Method) || parts is not ["", "api", "releases", var id, "download"] ||
            !long.TryParse(id, out var releaseId) || releaseId <= 0 ||
            !Guid.TryParse(context.Request.Query["downloadAttempt"], out var attempt) || attempt == Guid.Empty ||
            context.Items[BasicAuthMiddleware.AuthPassedKey] is not true ||
            !sessions.TryGet(context, out var session) || !diagnostics.TryStart(session, releaseId, attempt))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        try
        {
            // Keep the existing authentication, filters, proxy and streaming execution intact.
            await next(context).ConfigureAwait(false);
            var magnet = context.Response.StatusCode == 302 &&
                context.Response.Headers.Location.ToString().StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);
            diagnostics.Finish(session, releaseId, attempt, context.Response.StatusCode, magnet,
                context.Items[DownloadDiagnostics.FailureCodeKey] as string);
        }
        catch (Exception ex)
        {
            var network = ex is IOException or HttpRequestException or OperationCanceledException;
            diagnostics.Finish(session, releaseId, attempt,
                network ? StatusCodes.Status502BadGateway : StatusCodes.Status500InternalServerError,
                false, network ? "network_error" : "server_error");
            throw;
        }
    }
}
