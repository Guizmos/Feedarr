using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Feedarr.Api.Controllers;

// Web login and session state; explicit API Basic remains supported.
[ApiController]
[Route("api/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(
    SettingsRepository settings,
    SetupStateService setup,
    WebSessionService sessions,
    AuthThrottleService throttle) : ControllerBase
{
    public sealed record LoginRequest(string? Username, string? Password, bool RememberMe = false);

    [HttpPost("login")]
    [EnableRateLimiting("web-login")]
    [RequestSizeLimit(8192)]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        if (!WebSessionSecurity.HasSameOrigin(Request)) return ForbiddenCsrf();
        if (!Request.IsHttps) return Failure(400, "https_required");

        // Read the generation first: changes during validation make Create fail closed.
        var generation = sessions.Generation();
        var security = settings.GetSecurity(new SecuritySettings());
        if (!SmartAuthPolicy.IsAuthConfigured(security, null))
            return !setup.IsSetupCompleted() ? Failure(403, "setup_required") : Failure(409, "credentials_not_configured");

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
        var session = SmartAuthPolicy.IsAuthConfigured(security, null)
            ? sessions.Authenticate(HttpContext) : null;
        return Ok(State(security, session));
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        if (!WebSessionSecurity.HasSameOrigin(Request)) return ForbiddenCsrf();
        if (!Request.IsHttps) return Failure(400, "https_required");
        var session = sessions.Authenticate(HttpContext);
        if (session is not null && !WebSessionSecurity.HasValidCsrf(Request, session)) return ForbiddenCsrf();
        sessions.Revoke(HttpContext);
        return Ok(new { authenticated = false });
    }

    private object State(SecuritySettings security, WebSession? session) => new
    {
        authenticated = session is not null,
        authRequired = SmartAuthPolicy.IsAuthRequired(HttpContext, security),
        authConfigured = SmartAuthPolicy.IsAuthConfigured(security, null),
        authMode = SmartAuthPolicy.NormalizeAuthMode(security),
        setupCompleted = setup.IsSetupCompleted(),
        webSessionAvailable = Request.IsHttps && SmartAuthPolicy.IsAuthConfigured(security, null),
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
