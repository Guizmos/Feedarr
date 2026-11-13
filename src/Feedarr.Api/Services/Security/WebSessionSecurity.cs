using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Feedarr.Api.Services.Security;

internal static class WebSessionSecurity
{
    internal static void AddLoginRateLimit(RateLimiterOptions options)
        => options.AddPolicy("web-login", context => RateLimitPartition.GetFixedWindowLimiter(
            "web-login:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
            }));

    private static readonly HashSet<string> PublicShellPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/", "/index.html", "/login", "/create-admin", "/setup", "/library", "/library/top", "/activity", "/history",
        "/indexers", "/system", "/system/statistics", "/system/indexers", "/system/storage", "/system/providers",
        "/system/updates", "/settings", "/settings/ui", "/settings/providers", "/settings/indexers",
        "/settings/externals", "/settings/applications", "/settings/users", "/settings/maintenance",
        "/settings/backup", "/settings/about"
    };

    internal static bool IsPublicShell(HttpRequest request)
        => (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) &&
           PublicShellPaths.Contains(request.Path.Value ?? "");

    internal static bool IsBrowserApiRequest(HttpRequest request)
        => !HasExplicitBasic(request) && request.Path.StartsWithSegments("/api") &&
           (request.Headers["X-Feedarr-Web"] == "1" ||
            request.Headers["Sec-Fetch-Mode"] == "navigate" ||
            (request.Headers["Sec-Fetch-Site"] == "same-origin" && request.Headers["Sec-Fetch-Dest"] == "image"));

    internal static void DisableCaching(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.OnStarting(() =>
        {
            // Controllers (e.g. posters) can otherwise replace this with public caching.
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
    }

    internal const string SessionKey = "feedarr.auth.web_session";
    internal const string CsrfHeader = "X-Feedarr-CSRF";

    internal static bool HasExplicitBasic(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString().TrimStart();
        return authorization.Equals("Basic", StringComparison.OrdinalIgnoreCase) ||
               (authorization.Length > 5 && authorization.StartsWith("Basic", StringComparison.OrdinalIgnoreCase) &&
                char.IsWhiteSpace(authorization[5]));
    }

    internal static bool IsAuthEndpoint(HttpRequest request)
        => (HttpMethods.IsPost(request.Method) &&
            (request.Path.Equals("/api/auth/create-admin", StringComparison.OrdinalIgnoreCase) ||
             request.Path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
             request.Path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase))) ||
           (HttpMethods.IsGet(request.Method) && request.Path.Equals("/api/auth/session", StringComparison.OrdinalIgnoreCase));

    // Use only Scheme/Host already processed by trusted ForwardedHeadersMiddleware.
    // Cookie mutations deliberately do not inherit the legacy origin allowlist.
    internal static bool HasSameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin))
        {
            if (!RequestForgeryProtection.TryGetRefererOrigin(request.Headers.Referer.ToString(), out origin))
                return false;
        }
        return RequestForgeryProtection.TryNormalizeOrigin(origin, out var candidate) &&
               RequestForgeryProtection.TryNormalizeCurrentRequestOrigin(request, out var current) &&
               string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase);
    }

    internal static string CsrfFor(string token)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), "Feedarr session CSRF v1"u8));

    internal static bool HasValidCsrf(HttpRequest request, WebSession session)
    {
        var provided = request.Headers[CsrfHeader].ToString();
        return provided.Length == session.CsrfToken.Length &&
               CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(session.CsrfToken));
    }
}

public sealed record WebSession(string CsrfToken, DateTimeOffset ExpiresAt, DateTimeOffset AbsoluteExpiresAt, bool RememberMe);
