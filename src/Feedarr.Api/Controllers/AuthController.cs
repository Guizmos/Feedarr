using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;

namespace Feedarr.Api.Controllers;

// Web login and session state; explicit API Basic remains supported.
[ApiController]
[Route("api/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(
    SettingsRepository settings,
    WebSessionService sessions,
    AuthThrottleService throttle) : ControllerBase
{
    public sealed record LoginRequest(string? Username, string? Password, bool RememberMe = false);

    public sealed record CreateAdminRequest(string? Username, string? Password, string? PasswordConfirmation);

    [HttpPost("create-admin")]
    [EnableRateLimiting("web-login")]
    [RequestSizeLimit(8192)]
    public IActionResult CreateAdmin([FromBody] CreateAdminRequest request,
        [FromServices] IConfiguration configuration,
        [FromServices] IMemoryCache cache)
    {
        if (!WebSessionSecurity.HasSameOrigin(Request)) return ForbiddenCsrf();
        if (!settings.RequiresInitialAdministrator()) return Failure(409, "administrator_already_created");
        var username = (request.Username ?? "").Trim();
        if (username.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(username) > 512 ||
            request.Password is null || request.Password.Length > 4096 || request.Password != request.PasswordConfirmation)
            return Failure(400, "invalid_administrator_credentials");
        if (!CredentialValidator.TryValidatePasswordStrength(request.Password, out _, out _))
            return Failure(400, "password_complexity_required");
        var (hash, salt) = CredentialValidator.HashPassword(request.Password);
        var security = new SecuritySettings { RequireHttps = settings.GetSecurity(new()).RequireHttps,
            Username = username, PasswordHash = hash, PasswordSalt = salt };
        if (!AuthenticationPolicy.CanUseTransport(Request, security, configuration)) return Failure(403, "https_required");
        if (!settings.TryCreateInitialAdministrator(security)) return Failure(409, "administrator_already_created");
        cache.Remove(SecuritySettingsCache.CacheKey);
        var session = sessions.Create(HttpContext, sessions.Generation(), false);
        return Ok(State(security, session));
    }

    [HttpPost("login")]
    [EnableRateLimiting("web-login")]
    [RequestSizeLimit(8192)]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        if (!WebSessionSecurity.HasSameOrigin(Request)) return ForbiddenCsrf();

        if (!AuthenticationPolicy.CanUseTransport(Request, settings.GetSecurity(new()), HttpContext.RequestServices?.GetService<IConfiguration>())) return Failure(403, "https_required");

        // Read the generation first: changes during validation make Create fail closed.
        var generation = sessions.Generation();
        var security = settings.GetSecurity(new SecuritySettings());
        if (!AuthenticationPolicy.IsAuthConfigured(security))
            return Failure(409, "credentials_not_configured");

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var username = request.Username ?? "";
        if (throttle.TryGetRetryAfter(ip, username, out var retryAfter))
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Failure(429, "too_many_auth_attempts", seconds);
        }
        if (username.Length > 512 || request.Password is null || request.Password.Length > 4096 ||
            !CredentialValidator.Validate(username, request.Password, security))
        {
            throttle.RegisterFailure(ip, username);
            return Failure(401, "invalid_credentials");
        }
        var session = sessions.Create(HttpContext, generation, request.RememberMe);
        if (session is null) return Failure(409, "security_configuration_changed");
        throttle.RegisterSuccess(ip, username);
        return Ok(State(security, session));
    }

    [HttpGet("session")]
    public IActionResult Session()
    {
        var security = settings.GetSecurity(new SecuritySettings());
        var session = AuthenticationPolicy.IsAuthConfigured(security)
            ? sessions.Authenticate(HttpContext) : null;
        return Ok(State(security, session));
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        if (!WebSessionSecurity.HasSameOrigin(Request)) return ForbiddenCsrf();
        if (!AuthenticationPolicy.CanUseTransport(Request, settings.GetSecurity(new()), HttpContext.RequestServices?.GetService<IConfiguration>())) return Failure(403, "https_required");
        var session = sessions.Authenticate(HttpContext);
        if (session is not null && !WebSessionSecurity.HasValidCsrf(Request, session)) return ForbiddenCsrf();
        sessions.Revoke(HttpContext);
        return Ok(new { authenticated = false });
    }

    private object State(SecuritySettings security, WebSession? session) => new
    {
        authenticated = session is not null,
        authRequired = AuthenticationPolicy.IsAuthRequired(HttpContext, security),
        authConfigured = AuthenticationPolicy.IsAuthConfigured(security),
        requireHttps = security.RequireHttps,
        initialAdminRequired = settings.RequiresInitialAdministrator(),
        secureTransport = Request.IsHttps,
        setupCompleted = true, // Compatibility only: configuration is no longer a prerequisite for access.
        webSessionAvailable = AuthenticationPolicy.CanUseTransport(Request, security, HttpContext.RequestServices?.GetService<IConfiguration>()) && AuthenticationPolicy.IsAuthConfigured(security),
        httpRecovery = HttpContext.RequestServices?.GetService<IConfiguration>()?.GetValue<bool>("Security:AllowHttpRecovery") ?? false,
        csrfToken = session?.CsrfToken,
        expiresAt = session?.ExpiresAt,
        absoluteExpiresAt = session?.AbsoluteExpiresAt,
        rememberMe = session?.RememberMe ?? false
    };

    private ObjectResult ForbiddenCsrf() => Failure(403, "cross_site_request_blocked");

    private ObjectResult Failure(int status, string code, int? retryAfterSeconds = null)
    {
        var problem = new ProblemDetails { Status = status, Title = code };
        problem.Extensions["code"] = code;
        if (retryAfterSeconds.HasValue) problem.Extensions["retryAfterSeconds"] = retryAfterSeconds.Value;
        return StatusCode(status, problem);
    }
}
