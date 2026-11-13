using System;
using System.Linq;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/setup")]
public sealed class SetupController : ControllerBase
{
    private readonly OutboundDestinationPolicy _outboundPolicy;
    private readonly Feedarr.Api.Services.Backup.Configuration.ConfigurationRestoreService? _configurationRestore;
    private readonly Db _db;
    private readonly SettingsRepository _settings;
    private readonly ProviderRepository _providers;
    private readonly IConfiguration _config;
    private readonly BootstrapTokenService _bootstrapTokens;
    private readonly SetupStateService _setupState;
    private readonly ILogger<SetupController> _log;

    public SetupController(
        Db db,
        SettingsRepository settings,
        ProviderRepository providers,
        IConfiguration config,
        BootstrapTokenService bootstrapTokens,
        SetupStateService setupState,
        ILogger<SetupController> log, OutboundDestinationPolicy? outboundPolicy = null,
        Feedarr.Api.Services.Backup.Configuration.ConfigurationRestoreService? configurationRestore = null)
    {
        _configurationRestore = configurationRestore;
        _outboundPolicy = outboundPolicy ?? new OutboundDestinationPolicy(new Feedarr.Api.Options.OutboundSecurityOptions());
        _db = db;
        _settings = settings;
        _providers = providers;
        _config = config;
        _bootstrapTokens = bootstrapTokens;
        _setupState = setupState;
        _log = log;
    }

    // GET /api/setup/state
    [HttpGet("state")]
    public IActionResult State()
    {
        using var conn = _db.Open();
        var sources = conn.Query<string>("SELECT torznab_url FROM sources;").ToList();

        var hasSources = sources.Count > 0;
        var hasJackettSource = sources.Any(url =>
            !string.IsNullOrWhiteSpace(url) &&
            url.Contains("/api/v2.0/indexers/", StringComparison.OrdinalIgnoreCase));

        var hasProwlarrSource = sources.Any(url =>
            !string.IsNullOrWhiteSpace(url) &&
            (Regex.IsMatch(url, @"/\d+/api/?(\?|$)", RegexOptions.IgnoreCase) ||
             url.Contains("/api/v1/search", StringComparison.OrdinalIgnoreCase) ||
             url.Contains("prowlarr", StringComparison.OrdinalIgnoreCase)));

        var ui = _settings.GetUi(new UiSettings());
        var security = _settings.GetSecurity(new SecuritySettings());
        var bootstrapSecret = AuthenticationPolicy.GetBootstrapSecret(_config);
        var authConfigured = AuthenticationPolicy.IsAuthConfigured(security, bootstrapSecret);
        var authRequired = AuthenticationPolicy.IsAuthRequired(HttpContext, security);
        var requireHttps = security.RequireHttps;

        return Ok(new
        {
            onboardingDone = ui.OnboardingDone,
            hasSources,
            hasJackettSource,
            hasProwlarrSource,
            requireHttps,
            authConfigured,
            authRequired,
            restoreSteps = _configurationRestore?.GetResumeStepsForSetup()
        });
    }

    public sealed class SetupIndexerProviderUpsertDto
    {
        public string BaseUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public bool? Enabled { get; set; }
    }

    // PUT /api/setup/indexer-providers/{type}
    [HttpPut("indexer-providers/{type}")]
    public IActionResult UpsertIndexerProvider([FromRoute] string type, [FromBody] SetupIndexerProviderUpsertDto dto)
    {
        var normalizedType = NormalizeType(type);
        if (normalizedType is null)
            return Problem(title: "provider type invalid", statusCode: StatusCodes.Status400BadRequest);

        if (!OutboundUrlGuard.TryNormalizeProviderBaseUrl(normalizedType, dto.BaseUrl, out var normalizedBaseUrl, out var baseUrlError, policy: _outboundPolicy, context: OutboundDestinationContext.TrustedIntegration))
            return Problem(title: baseUrlError, statusCode: StatusCodes.Status400BadRequest);

        var apiKey = (dto.ApiKey ?? "").Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
            return Problem(title: "apiKey missing", statusCode: StatusCodes.Status400BadRequest);

        var enabled = dto.Enabled ?? true;
        var name = normalizedType.Equals("prowlarr", StringComparison.OrdinalIgnoreCase) ? "Prowlarr" : "Jackett";
        var id = _providers.UpsertByType(normalizedType, name, normalizedBaseUrl, apiKey, enabled);

        _log.LogInformation(
            "Setup provider persisted: type={Type} id={Id} baseUrl={BaseUrl} enabled={Enabled}",
            normalizedType,
            id,
            normalizedBaseUrl,
            enabled);

        return Ok(new
        {
            id,
            type = normalizedType,
            name,
            baseUrl = normalizedBaseUrl,
            enabled
        });
    }

    private static string? NormalizeType(string? value)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        if (normalized == "jackett" || normalized == "prowlarr") return normalized;
        return null;
    }

    // POST /api/setup/bootstrap-token
    [HttpPost("bootstrap-token")]
    [EnableRateLimiting("bootstrap-token")]
    public IActionResult IssueBootstrapToken()
    {
        // Initial setup and recovery with missing credentials use the same scoped token.
        // An installed instance with credentials must use normal authentication.
        if (_setupState.IsSetupCompleted() && AuthenticationPolicy.IsAuthConfigured(_settings.GetSecurity(new SecuritySettings()), null))
        {
            _log.LogWarning(
                "Bootstrap token request rejected: setup is already completed (remoteIp={RemoteIp})",
                HttpContext.Connection.RemoteIpAddress?.ToString() ?? "");
            return Conflict(new
            {
                error = "setup_already_completed",
                message = "Bootstrap tokens can only be issued during initial setup. Setup has already been completed."
            });
        }

        var bootstrapSecret = AuthenticationPolicy.GetBootstrapSecret(_config);
        var allowed =
            AuthenticationPolicy.IsLoopbackRequest(HttpContext) ||
            AuthenticationPolicy.HasValidBootstrapSecretHeader(HttpContext, bootstrapSecret);

        if (!allowed)
        {
            var hasBootstrapHeader =
                HttpContext.Request.Headers.ContainsKey(AuthenticationPolicy.BootstrapSecretHeader) ||
                HttpContext.Request.Headers.ContainsKey(AuthenticationPolicy.LegacyBootstrapSecretHeader);
            _log.LogWarning(
                "Bootstrap token request rejected at controller for {Method} {Path} (remoteIp={RemoteIp}, hasBootstrapHeader={HasBootstrapHeader})",
                HttpContext.Request.Method,
                HttpContext.Request.Path.Value ?? "/",
                HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
                hasBootstrapHeader);
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "bootstrap_secret_required",
                message = "Bootstrap token requires loopback access or a valid X-Bootstrap-Secret header"
            });
        }

        var token = _bootstrapTokens.IssueToken();
        return Ok(new
        {
            token,
            expiresInSeconds = _bootstrapTokens.ExpiresInSeconds
        });
    }

}
