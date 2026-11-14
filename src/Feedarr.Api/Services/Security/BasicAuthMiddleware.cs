using System.Security.Cryptography;
using System.Text;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace Feedarr.Api.Services.Security;

public sealed class BasicAuthMiddleware
{
    internal const string AuthPassedKey = "feedarr.auth.passed";


    private readonly RequestDelegate _next;
    private readonly IConfiguration _config;

    public BasicAuthMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _config = config;
    }

    public async Task InvokeAsync(
        HttpContext context,
        SettingsRepository settingsRepo,
        IMemoryCache cache,
        AuthThrottleService authThrottle,
        ILogger<BasicAuthMiddleware> log,
        WebSessionService? webSessions = null)
    {
        // Docker's readiness probe must reach the SQLite check without credentials.
        // Exact GET only: do not open /health/deep or mark this as authenticated.
        if (HttpMethods.IsGet(context.Request.Method) &&
            context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Only neutral React HTML and explicitly public assets, never API/data routes.
        if (WebSessionSecurity.IsPublicShell(context.Request))
        {
            context.Response.Headers["X-Feedarr-Shell"] = "public-v1";
            context.Items[AuthPassedKey] = true;
            await _next(context).ConfigureAwait(false);
            return;
        }
        if (IsPublicStaticAssetRequest(context.Request))
        {
            context.Items[AuthPassedKey] = true;
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (WebSessionSecurity.IsAuthEndpoint(context.Request) && !WebSessionSecurity.HasExplicitBasic(context.Request))
        {
            WebSessionSecurity.DisableCaching(context);
            context.Items[AuthPassedKey] = true;
            await _next(context).ConfigureAwait(false);
            return;
        }

        var security = cache.GetOrCreate(SecuritySettingsCache.CacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = SecuritySettingsCache.Duration;
            return settingsRepo.GetSecurity(new SecuritySettings());
        }) ?? new SecuritySettings();

        var authConfigured = AuthenticationPolicy.IsAuthConfigured(security);
        if (!AuthenticationPolicy.CanUseTransport(context.Request, security, _config))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "https_required", message = "HTTPS est obligatoire. Récupération administrateur : Security__AllowHttpRecovery=true sur le serveur, puis connexion avec vos identifiants." });
            return;
        }
        if (!authConfigured)
        {
            await RejectSecuritySetupRequired(context).ConfigureAwait(false);
            return;
        }

        if (webSessions is not null &&
            !WebSessionSecurity.HasExplicitBasic(context.Request))
        {
            var session = webSessions.Authenticate(context);
            if (session is not null)
            {
                context.Items[WebSessionSecurity.SessionKey] = session;
                context.Items[AuthPassedKey] = true;
                WebSessionSecurity.DisableCaching(context);
                await _next(context).ConfigureAwait(false);
                return;
            }
        }

        if (!TryGetBasicCredentials(context, out var user, out var pass))
        {
            await ChallengeAsync(context).ConfigureAwait(false);
            return;
        }

        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (authThrottle.TryGetRetryAfter(remoteIp, user, out var retryAfter))
        {
            await RejectThrottledAsync(context, retryAfter).ConfigureAwait(false);            return;
        }

        // Fast path: skip the PBKDF2 computation if this credential pair was recently validated.
        // The cache key is an HMAC-SHA256 of (user:pass) keyed with the stored password salt,
        // so it auto-invalidates whenever the password (and therefore the salt) changes.
        var credCacheKey = ComputeCredCacheKey(user, pass, security, log);
        if (credCacheKey is not null && cache.TryGetValue<bool>(credCacheKey, out var cachedValid) && cachedValid)
        {
            authThrottle.RegisterSuccess(remoteIp, user);
            context.Items[AuthPassedKey] = true;
            await _next(context).ConfigureAwait(false);            return;
        }

        if (!CredentialValidator.Validate(user, pass, security))
        {
            authThrottle.RegisterFailure(remoteIp, user);
            await ChallengeAsync(context).ConfigureAwait(false);
            return;
        }

        // Only cache successes — caching failures would block re-login after a password change.
        if (credCacheKey is not null)
            cache.Set(credCacheKey, true, TimeSpan.FromMinutes(5));

        authThrottle.RegisterSuccess(remoteIp, user);
        context.Items[AuthPassedKey] = true;
        await _next(context).ConfigureAwait(false);    }

    private static async Task ChallengeAsync(HttpContext context)
    {
        if (WebSessionSecurity.IsBrowserApiRequest(context.Request))
        {
            WebSessionSecurity.DisableCaching(context);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            // No redirect and no native Basic prompt on the explicit web path.
            await context.Response.WriteAsJsonAsync(new { error = "authentication_required" }).ConfigureAwait(false);
            return;
        }
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"Feedarr\"";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }

    private static Task RejectThrottledAsync(HttpContext context, TimeSpan retryAfter)
    {
        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        context.Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new
        {
            error = "too_many_auth_attempts",
            retryAfterSeconds
        });
    }

    private static Task RejectSecuritySetupRequired(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new
        {
            error = "security_setup_required",
            message = "Security setup is required before accessing this endpoint",
            authRequired = true,
            authConfigured = false
        });
    }

    /// <summary>
    /// Computes a short-lived cache key that represents a validated credential pair.
    /// Using HMAC keyed with the password salt means the key changes automatically when
    /// the password (and therefore the salt) is updated — no explicit cache invalidation needed.
    /// Returns null if the salt is unavailable or malformed.
    /// </summary>
    private static string? ComputeCredCacheKey(string user, string pass, SecuritySettings settings, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(settings.PasswordSalt)) return null;
        try
        {
            var saltBytes = Convert.FromBase64String(settings.PasswordSalt);
            var input = Encoding.UTF8.GetBytes(settings.Username + "\0" + settings.PasswordHash + "\0" + user + ":" + pass);
            using var hmac = new HMACSHA256(saltBytes);
            return "auth:ok:" + Convert.ToBase64String(hmac.ComputeHash(input));
        }
        catch (FormatException)
        {
            // Salt in DB is not valid Base64 — credential cache unavailable, fall through to PBKDF2.
            log.LogWarning("Auth credential cache unavailable: PasswordSalt is not valid Base64");
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Auth credential cache unavailable: unexpected error computing cache key");
            return null;
        }
    }

    private static bool TryGetBasicCredentials(HttpContext context, out string user, out string pass)
    {
        user = "";
        pass = "";

        if (!context.Request.Headers.TryGetValue("Authorization", out var authHeader))
            return false;

        var raw = authHeader.ToString();
        if (!raw.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;

        var encoded = raw[6..].Trim();
        if (string.IsNullOrWhiteSpace(encoded))
            return false;

        try
        {
            var bytes = Convert.FromBase64String(encoded);
            var decoded = Encoding.UTF8.GetString(bytes);
            var idx = decoded.IndexOf(':');
            if (idx <= 0) return false;
            user = decoded[..idx];
            pass = decoded[(idx + 1)..];
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPublicStaticAssetRequest(HttpRequest request)
    {
        if (!(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)))
            return false;

        var path = request.Path.Value ?? "";
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if ((path.Equals("/assets", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (path.Equals("/pwa-assets.json", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/manifest.webmanifest", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/service-worker.js", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/icon-192.png", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/icon-512.png", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/icon-maskable-512.png", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/feedarr-logo.png", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.StartsWith("/favicon-", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/favicon.", StringComparison.OrdinalIgnoreCase);
    }

}
