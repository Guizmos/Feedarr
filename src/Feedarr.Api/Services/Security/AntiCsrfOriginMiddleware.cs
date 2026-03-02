using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Feedarr.Api.Services.Security;

public sealed class AntiCsrfOriginMiddleware
{
    private static readonly string[] ExemptApiPrefixes =
    {
        "/api/security",
        "/api/settings/security",
        "/api/setup/state",
        "/api/setup/bootstrap-token",
        "/api/system/onboarding"
    };

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
        if (IsExemptPath(request.Path.Value))
        {
            await _next(context);
            return;
        }

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
            if (IsSameOrigin(request, origin))
            {
                await _next(context);
                return;
            }

            if (RequestForgeryProtection.IsAllowedOrigin(request, configuration, origin))
            {
                await _next(context);
                return;
            }

            log.LogWarning(
                "Cross-site blocked: Origin={Origin}, Scheme={Scheme}, Host={Host}, XFP={XFP}, XFH={XFH}",
                origin,
                context.Request.Scheme,
                context.Request.Host,
                context.Request.Headers["X-Forwarded-Proto"].ToString(),
                context.Request.Headers["X-Forwarded-Host"].ToString());
            await RejectAsync(context, log, "origin_not_allowed", origin);
            return;
        }

        var hasReferer = request.Headers.TryGetValue("Referer", out var refererValues) &&
                         !string.IsNullOrWhiteSpace(refererValues.ToString());
        if (hasReferer)
        {
            var referer = refererValues.ToString().Trim();
            if (RequestForgeryProtection.TryGetRefererOrigin(referer, out var refererOrigin) &&
                (IsSameOrigin(request, refererOrigin) ||
                 RequestForgeryProtection.IsAllowedOrigin(request, configuration, refererOrigin)))
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

    private static bool IsExemptPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        foreach (var prefix in ExemptApiPrefixes)
        {
            if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSameOrigin(HttpRequest request, string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var candidate))
            return false;

        var publicScheme = GetPublicScheme(request);
        var publicHost = GetPublicHost(request);
        if (!string.Equals(candidate.Scheme, publicScheme, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(candidate.IdnHost, publicHost.Host, StringComparison.OrdinalIgnoreCase))
            return false;

        var publicPort = publicHost.Port ?? DefaultPort(publicScheme);
        var originPort = candidate.IsDefaultPort ? DefaultPort(candidate.Scheme) : candidate.Port;
        return publicPort == originPort;
    }

    private static string GetPublicScheme(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-Forwarded-Proto", out var values))
        {
            var forwarded = values.ToString().Split(',')[0].Trim();
            if (!string.IsNullOrWhiteSpace(forwarded))
                return forwarded;
        }

        return request.Scheme;
    }

    private static HostString GetPublicHost(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-Forwarded-Host", out var values))
        {
            var forwarded = values.ToString().Split(',')[0].Trim();
            if (!string.IsNullOrWhiteSpace(forwarded))
                return new HostString(forwarded);
        }

        return request.Host;
    }

    private static int DefaultPort(string scheme)
        => string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;
}
