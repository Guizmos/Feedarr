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

    private static readonly string[] SetupAllowedApiPrefixes =
    {
        "/api/setup",
        "/api/system/onboarding",
        "/api/settings/ui",
        "/api/settings/security",
        "/api/providers",
        "/api/sources",
        "/api/apps",
        "/api/categories",
        "/api/jackett/indexers",
        "/api/prowlarr/indexers"
    };

    private static readonly string[] SecuritySetupAllowedApiPrefixes =
    {
        "/api/setup/state",
        "/api/system/onboarding",
        "/api/settings/ui",
        "/api/settings/security"
    };

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
        SetupStateService setupState,
        ILogger<BasicAuthMiddleware> log)
    {
        if (!setupState.IsSetupCompleted() && !IsAllowedDuringSetup(context.Request))
        {
            log.LogWarning(
                "Request rejected by setup lock for {Method} {Path}",
                context.Request.Method,
                context.Request.Path.Value ?? "/");
            await HandleSetupLockRejection(context);
            return;
        }

        var security = cache.GetOrCreate(SecuritySettingsCache.CacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = SecuritySettingsCache.Duration;
            return settingsRepo.GetSecurity(new SecuritySettings());
        }) ?? new SecuritySettings();

        var bootstrapSecret = SmartAuthPolicy.GetBootstrapSecret(_config);
        if (SmartAuthPolicy.IsBootstrapTokenRequest(context.Request))
        {
            if (SmartAuthPolicy.IsLoopbackRequest(context) ||
                SmartAuthPolicy.HasValidBootstrapSecretHeader(context, bootstrapSecret))
            {
                context.Items[AuthPassedKey] = true;
                await _next(context);
                return;
            }

            var hasBootstrapHeader =
                context.Request.Headers.ContainsKey(SmartAuthPolicy.BootstrapSecretHeader) ||
                context.Request.Headers.ContainsKey(SmartAuthPolicy.LegacyBootstrapSecretHeader);
            log.LogWarning(
                "Bootstrap token request rejected for {Method} {Path} (remoteIp={RemoteIp}, hasBootstrapHeader={HasBootstrapHeader})",
                context.Request.Method,
                context.Request.Path.Value ?? "/",
                context.Connection.RemoteIpAddress?.ToString() ?? "",
                hasBootstrapHeader);
            await RejectBootstrapTokenDenied(context);
            return;
        }

        var authRequired = SmartAuthPolicy.IsAuthRequired(context, security);
        var authConfigured = SmartAuthPolicy.IsAuthConfigured(security, bootstrapSecret);
        var authMode = SmartAuthPolicy.NormalizeAuthMode(security);

        if (!authRequired)
        {
            context.Items[AuthPassedKey] = true;
            await _next(context);
            return;
        }

        if (!authConfigured)
        {
            if (IsAllowedDuringSecuritySetup(context.Request))
            {
                context.Items[AuthPassedKey] = true;
                await _next(context);
                return;
            }

            log.LogWarning(
                "Request rejected by security setup lock for {Method} {Path} (mode={AuthMode})",
                context.Request.Method,
                context.Request.Path.Value ?? "/",
                authMode);
            await RejectSecuritySetupRequired(context);
            return;
        }

        if (SmartAuthPolicy.HasValidBootstrapSecretHeader(context, bootstrapSecret))
        {
            context.Items[AuthPassedKey] = true;
            await _next(context);
            return;
        }

        if (!TryGetBasicCredentials(context, out var user, out var pass))
        {
            Challenge(context);
            return;
        }

        if (!Validate(user, pass, security))
        {
            Challenge(context);
            return;
        }

        context.Items[AuthPassedKey] = true;
        await _next(context);
    }

    private static void Challenge(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"Feedarr\"";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }

    private static Task HandleSetupLockRejection(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (!string.IsNullOrWhiteSpace(path) &&
            path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            return RejectSetupRequiredApi(context);
        }

        context.Response.Redirect("/setup", permanent: false);
        return Task.CompletedTask;
    }

    private static Task RejectSetupRequiredApi(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new
        {
            error = "setup_required",
            message = "Setup required"
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

    private static Task RejectBootstrapTokenDenied(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new
        {
            error = "bootstrap_secret_required",
            message = "Bootstrap token requires loopback access or a valid X-Bootstrap-Secret header"
        });
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

    private static bool Validate(string user, string pass, SecuritySettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Username)) return false;
        if (!string.Equals(user, settings.Username, StringComparison.Ordinal)) return false;
        if (string.IsNullOrWhiteSpace(settings.PasswordHash) || string.IsNullOrWhiteSpace(settings.PasswordSalt)) return false;

        return VerifyPassword(pass, settings.PasswordHash, settings.PasswordSalt);
    }

    private static bool VerifyPassword(string password, string hashBase64, string saltBase64)
    {
        try
        {
            var salt = Convert.FromBase64String(saltBase64);
            var expected = Convert.FromBase64String(hashBase64);
            using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
            var actual = pbkdf2.GetBytes(expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAllowedDuringSetup(HttpRequest request)
    {
        if (HttpMethods.IsOptions(request.Method))
            return true;

        var path = request.Path.Value ?? "";
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
            return true;

        if (PathEqualsOrUnder(path, "/setup") ||
            PathEqualsOrUnder(path, "/assets") ||
            path.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/manifest", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/service-worker", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var prefix in SetupAllowedApiPrefixes)
        {
            if (PathEqualsOrUnder(path, prefix))
                return true;
        }

        return false;
    }

    private static bool IsAllowedDuringSecuritySetup(HttpRequest request)
    {
        if (HttpMethods.IsOptions(request.Method))
            return true;

        var path = request.Path.Value ?? "";
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (SmartAuthPolicy.IsBootstrapTokenRequest(request))
            return true;

        if (HttpMethods.IsGet(request.Method) &&
            (PathEqualsOrUnder(path, "/api/settings/security") ||
             PathEqualsOrUnder(path, "/api/setup/state")))
            return true;

        if (path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
            return true;

        if (PathEqualsOrUnder(path, "/setup") ||
            PathEqualsOrUnder(path, "/assets") ||
            path.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/manifest", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/service-worker", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var prefix in SecuritySetupAllowedApiPrefixes)
        {
            if (PathEqualsOrUnder(path, prefix))
                return true;
        }

        return false;
    }

    private static bool PathEqualsOrUnder(string path, string prefix)
    {
        if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            return true;

        return path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }
}
