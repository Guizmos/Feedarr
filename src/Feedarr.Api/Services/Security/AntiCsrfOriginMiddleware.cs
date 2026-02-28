using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Feedarr.Api.Services.Security;

public sealed class AntiCsrfOriginMiddleware
{
    private readonly RequestDelegate _next;

    public AntiCsrfOriginMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IConfiguration configuration,
        ILogger<AntiCsrfOriginMiddleware> log)
    {
        var request = context.Request;
        if (RequestForgeryProtection.IsSafeMethod(request.Method))
        {
            await _next(context);
            return;
        }

        var hasOrigin = request.Headers.TryGetValue("Origin", out var originValues) &&
                        !string.IsNullOrWhiteSpace(originValues.ToString());
        if (hasOrigin)
        {
            var origin = originValues.ToString().Trim();
            if (RequestForgeryProtection.IsAllowedOrigin(request, configuration, origin))
            {
                await _next(context);
                return;
            }

            await RejectAsync(context, log, "origin_not_allowed", origin);
            return;
        }

        var hasReferer = request.Headers.TryGetValue("Referer", out var refererValues) &&
                         !string.IsNullOrWhiteSpace(refererValues.ToString());
        if (hasReferer)
        {
            var referer = refererValues.ToString().Trim();
            if (RequestForgeryProtection.TryGetRefererOrigin(referer, out var refererOrigin) &&
                RequestForgeryProtection.IsAllowedOrigin(request, configuration, refererOrigin))
            {
                await _next(context);
                return;
            }

            await RejectAsync(context, log, "referer_not_allowed", referer);
            return;
        }

        if (!RequestForgeryProtection.RequireExplicitHeaderForUnsafeMethods(configuration) ||
            RequestForgeryProtection.HasTrustedNonBrowserHeader(request.Headers))
        {
            await _next(context);
            return;
        }

        await RejectAsync(context, log, "trusted_request_header_missing", null);
    }

    private static Task RejectAsync(HttpContext context, ILogger log, string reason, string? candidate)
    {
        log.LogWarning(
            "Unsafe request blocked by CSRF/origin guard: method={Method} path={Path} reason={Reason} candidate={Candidate}",
            context.Request.Method,
            context.Request.Path.Value ?? "/",
            reason,
            string.IsNullOrWhiteSpace(candidate) ? "-" : candidate);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new
        {
            error = "cross_site_request_blocked",
            message = "Unsafe cross-site or untrusted request blocked",
            reason
        });
    }
}
