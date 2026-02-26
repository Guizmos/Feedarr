using Microsoft.AspNetCore.Mvc;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Fanart;
using Feedarr.Api.Services.Igdb;
using Feedarr.Api.Services.Tmdb;
using Feedarr.Api.Services.TvMaze;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/settings")]
public sealed class SettingsController : ControllerBase
{
    private readonly SettingsRepository _repo;
    private readonly AppOptions _app;
    private readonly TmdbClient _tmdb;
    private readonly FanartClient _fanart;
    private readonly IgdbClient _igdb;
    private readonly TvMazeClient _tvmaze;
    private readonly ExternalProviderInstanceRepository? _externalProviderInstances;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _config;
    private readonly ILogger<SettingsController> _log;

    public SettingsController(
        SettingsRepository repo,
        IOptions<AppOptions> app,
        TmdbClient tmdb,
        FanartClient fanart,
        IgdbClient igdb,
        TvMazeClient tvmaze,
        IMemoryCache cache,
        IConfiguration config,
        ILogger<SettingsController> log,
        ExternalProviderInstanceRepository? externalProviderInstances = null)
    {
        _repo = repo;
        _app = app.Value;
        _tmdb = tmdb;
        _fanart = fanart;
        _igdb = igdb;
        _tvmaze = tvmaze;
        _externalProviderInstances = externalProviderInstances;
        _cache = cache;
        _config = config;
        _log = log;
    }

    // --------------------
    // GENERAL
    // --------------------
    [HttpGet("general")]
    public IActionResult GetGeneral()
    {
        var defaults = new GeneralSettings
        {
            SyncIntervalMinutes = _app.SyncIntervalMinutes,
            RssLimit = _app.RssLimitPerCategory > 0 ? _app.RssLimitPerCategory : _app.RssLimit,
            RssLimitPerCategory = _app.RssLimitPerCategory > 0 ? _app.RssLimitPerCategory : _app.RssLimit,
            RssLimitGlobalPerSource = _app.RssLimitGlobalPerSource > 0 ? _app.RssLimitGlobalPerSource : 250,
            AutoSyncEnabled = true,
            ArrSyncIntervalMinutes = 60,
            ArrAutoSyncEnabled = true,
            RequestIntegrationMode = "arr"
        };

        return Ok(_repo.GetGeneral(defaults));
    }

    [HttpPut("general")]
    public IActionResult PutGeneral([FromBody] GeneralSettings dto)
    {
        var interval = Math.Clamp(dto.SyncIntervalMinutes, 1, 1440);
        var perCatRaw = dto.RssLimitPerCategory > 0
            ? dto.RssLimitPerCategory
            : (dto.RssLimit > 0 ? dto.RssLimit : _app.RssLimitPerCategory);
        if (perCatRaw <= 0) perCatRaw = _app.RssLimit > 0 ? _app.RssLimit : 50;
        var perCatLimit = Math.Clamp(perCatRaw, 1, 200);

        var globalRaw = dto.RssLimitGlobalPerSource > 0 ? dto.RssLimitGlobalPerSource : _app.RssLimitGlobalPerSource;
        if (globalRaw <= 0) globalRaw = 250;
        var globalLimit = Math.Clamp(globalRaw, 1, 2000);
        var arrInterval = Math.Clamp(dto.ArrSyncIntervalMinutes, 1, 1440);
        var requestMode = (dto.RequestIntegrationMode ?? "arr").Trim().ToLowerInvariant();
        if (requestMode is not ("arr" or "overseerr" or "jellyseerr" or "seer"))
            requestMode = "arr";

        var saved = new GeneralSettings
        {
            SyncIntervalMinutes = interval,
            RssLimit = perCatLimit,
            RssLimitPerCategory = perCatLimit,
            RssLimitGlobalPerSource = globalLimit,
            AutoSyncEnabled = dto.AutoSyncEnabled,
            ArrSyncIntervalMinutes = arrInterval,
            ArrAutoSyncEnabled = dto.ArrAutoSyncEnabled,
            RequestIntegrationMode = requestMode
        };

        _repo.SaveGeneral(saved);
        return Ok(saved);
    }

    // --------------------
    // UI
    // --------------------
    [HttpGet("ui")]
    public IActionResult GetUi()
    {
        var defaults = new UiSettings();
        return Ok(_repo.GetUi(defaults));
    }

    [HttpPut("ui")]
    public IActionResult PutUi([FromBody] UiSettings dto)
    {
        var uiLanguage = UiLanguageCatalog.NormalizeUiLanguage(dto.UiLanguage);
        var mediaInfoLanguage = UiLanguageCatalog.NormalizeMediaInfoLanguage(dto.MediaInfoLanguage);

        var view = (dto.DefaultView ?? "grid").Trim().ToLowerInvariant();
        if (view is not ("grid" or "list" or "banner" or "poster")) view = "grid";

        var sort = (dto.DefaultSort ?? "date").Trim().ToLowerInvariant();
        if (sort is not ("date" or "seeders" or "downloads")) sort = "date";

        var maxAge = (dto.DefaultMaxAgeDays ?? "").Trim();
        if (maxAge is not ("" or "1" or "2" or "3" or "7" or "15" or "30")) maxAge = "";

        var limit = dto.DefaultLimit;
        if (limit != 0 && limit != 50 && limit != 100 && limit != 200 && limit != 500) limit = 100;

        var filterSeen = (dto.DefaultFilterSeen ?? "").Trim();
        if (filterSeen is not ("" or "1" or "0")) filterSeen = "";

        var filterApp = (dto.DefaultFilterApplication ?? "").Trim();
        if (string.IsNullOrWhiteSpace(filterApp))
        {
            filterApp = "";
        }
        else if (!string.Equals(filterApp, "__hide_apps__", StringComparison.Ordinal) &&
                 !long.TryParse(filterApp, out _))
        {
            filterApp = "";
        }

        var filterSource = (dto.DefaultFilterSourceId ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(filterSource) && !long.TryParse(filterSource, out _))
            filterSource = "";

        var filterCategory = (dto.DefaultFilterCategoryId ?? "").Trim().ToLowerInvariant();
        var filterQuality = (dto.DefaultFilterQuality ?? "").Trim();
        if (filterQuality.Length > 64) filterQuality = filterQuality[..64];

        var theme = (dto.Theme ?? "light").Trim().ToLowerInvariant();
        if (theme is not ("light" or "dark" or "system")) theme = "light";

        var saved = new UiSettings
        {
            UiLanguage = uiLanguage,
            MediaInfoLanguage = mediaInfoLanguage,
            HideSeenByDefault = dto.HideSeenByDefault,
            ShowCategories = dto.ShowCategories,
            EnableMissingPosterView = dto.EnableMissingPosterView,
            DefaultView = view,
            DefaultSort = sort,
            DefaultMaxAgeDays = maxAge,
            DefaultLimit = limit,
            DefaultFilterSeen = filterSeen,
            DefaultFilterApplication = filterApp,
            DefaultFilterSourceId = filterSource,
            DefaultFilterCategoryId = filterCategory,
            DefaultFilterQuality = filterQuality,
            BadgeInfo = dto.BadgeInfo,
            BadgeWarn = dto.BadgeWarn,
            BadgeError = dto.BadgeError,
            Theme = theme,
            AnimationsEnabled = dto.AnimationsEnabled,
            OnboardingDone = dto.OnboardingDone
        };

        _repo.SaveUi(saved);
        return Ok(saved);
    }

    // --------------------
    // EXTERNAL (TMDB / FANART / IGDB)
    // --------------------
    [HttpGet("external")]
    public IActionResult GetExternal()
    {
        var ext = _repo.GetExternal(new ExternalSettings());

        var hasTmdb = !string.IsNullOrWhiteSpace(ext.TmdbApiKey);
        var hasTvmaze = !string.IsNullOrWhiteSpace(ext.TvmazeApiKey);
        var hasFanart = !string.IsNullOrWhiteSpace(ext.FanartApiKey);
        var hasIgdbId = !string.IsNullOrWhiteSpace(ext.IgdbClientId);
        var hasIgdbSecret = !string.IsNullOrWhiteSpace(ext.IgdbClientSecret);

        return Ok(new
        {
            hasTmdbApiKey = hasTmdb,
            hasTvmazeApiKey = hasTvmaze,
            hasFanartApiKey = hasFanart,
            hasIgdbClientId = hasIgdbId,
            hasIgdbClientSecret = hasIgdbSecret,
            tmdbEnabled = hasTmdb && ext.TmdbEnabled != false,
            tvmazeEnabled = ext.TvmazeEnabled != false,
            fanartEnabled = hasFanart && ext.FanartEnabled != false,
            igdbEnabled = hasIgdbId && hasIgdbSecret && ext.IgdbEnabled != false,
        });
    }

    // body: { tmdbApiKey?, fanartApiKey?, igdbClientId?, igdbClientSecret? }
    // null/"" => ne change pas
    [HttpPut("external")]
    public IActionResult PutExternal([FromBody] ExternalSettings dto)
    {
        if (dto is null) return Problem(title: "body missing", statusCode: StatusCodes.Status400BadRequest);

        var saved = _repo.SaveExternalPartial(dto);
        _externalProviderInstances?.UpsertFromLegacySettings(saved);

        var hasTmdb = !string.IsNullOrWhiteSpace(saved.TmdbApiKey);
        var hasTvmaze = !string.IsNullOrWhiteSpace(saved.TvmazeApiKey);
        var hasFanart = !string.IsNullOrWhiteSpace(saved.FanartApiKey);
        var hasIgdbId = !string.IsNullOrWhiteSpace(saved.IgdbClientId);
        var hasIgdbSecret = !string.IsNullOrWhiteSpace(saved.IgdbClientSecret);

        // on renvoie aussi le status (sans exposer les clés)
        return Ok(new
        {
            ok = true,
            hasTmdbApiKey = hasTmdb,
            hasTvmazeApiKey = hasTvmaze,
            hasFanartApiKey = hasFanart,
            hasIgdbClientId = hasIgdbId,
            hasIgdbClientSecret = hasIgdbSecret,
            tmdbEnabled = hasTmdb && saved.TmdbEnabled != false,
            tvmazeEnabled = saved.TvmazeEnabled != false,
            fanartEnabled = hasFanart && saved.FanartEnabled != false,
            igdbEnabled = hasIgdbId && hasIgdbSecret && saved.IgdbEnabled != false,
        });
    }

    public sealed class ExternalTestDto
    {
        public string? Kind { get; set; }
    }

    // POST /api/settings/external/test
    [HttpPost("external/test")]
    public async Task<IActionResult> TestExternal([FromBody] ExternalTestDto dto, CancellationToken ct)
    {
        var kind = (dto?.Kind ?? "").Trim().ToLowerInvariant();
        if (kind is not (ExternalProviderKeys.Tmdb or ExternalProviderKeys.Tvmaze or ExternalProviderKeys.Fanart or ExternalProviderKeys.Igdb))
            return Problem(title: "invalid kind", statusCode: StatusCodes.Status400BadRequest);

        try
        {
            if (kind == ExternalProviderKeys.Tmdb)
            {
                var ok = await _tmdb.TestApiKeyAsync(ct);
                return Ok(new { ok });
            }

            if (kind == ExternalProviderKeys.Tvmaze)
            {
                var ok = await _tvmaze.TestApiAsync(ct);
                return Ok(new { ok });
            }

            if (kind == ExternalProviderKeys.Fanart)
            {
                var ok = await _fanart.TestApiKeyAsync(ct);
                return Ok(new { ok });
            }

            var okIgdb = await _igdb.TestCredsAsync(ct);
            return Ok(new { ok = okIgdb });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "External provider test failed for kind={Kind}", kind);
            return Problem(
                title: "provider test failed",
                detail: "upstream provider unavailable",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    // --------------------
    // SECURITY
    // --------------------
    [HttpGet("security")]
    public IActionResult GetSecurity()
    {
        var defaults = new SecuritySettings();
        var sec = _repo.GetSecurity(defaults);
        var authMode = SmartAuthPolicy.NormalizeAuthMode(sec);
        var bootstrapSecret = SmartAuthPolicy.GetBootstrapSecret(_config);
        var authConfigured = SmartAuthPolicy.IsAuthConfigured(sec, bootstrapSecret);
        var authRequired = SmartAuthPolicy.IsAuthRequired(HttpContext, sec);
        var (authentication, authenticationRequired) = LegacyAuthProjection(authMode);

        return Ok(new
        {
            authMode,
            authentication,
            authenticationRequired,
            publicBaseUrl = sec.PublicBaseUrl ?? "",
            username = sec.Username ?? "",
            hasPassword = !string.IsNullOrWhiteSpace(sec.PasswordHash),
            authConfigured,
            authRequired
        });
    }

    public sealed class SecuritySettingsDto
    {
        public string? AuthMode { get; set; }
        public string? Authentication { get; set; }
        public string? AuthenticationRequired { get; set; }
        public string? PublicBaseUrl { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? PasswordConfirmation { get; set; }
    }

    [HttpPut("security")]
    public IActionResult PutSecurity([FromBody] SecuritySettingsDto dto)
    {
        if (dto is null) return Problem(title: "body missing", statusCode: StatusCodes.Status400BadRequest);

        var current = _repo.GetSecurity(new SecuritySettings());

        var legacyAuth = Normalize(dto.Authentication, current.Authentication, new[] { "none", "basic" });
        var legacyRequired = Normalize(dto.AuthenticationRequired, current.AuthenticationRequired, new[] { "local", "all" });
        var fallbackMode = SmartAuthPolicy.NormalizeAuthMode(current);
        var authMode = SmartAuthPolicy.NormalizeAuthMode(dto.AuthMode ?? LegacyModeProjection(legacyAuth, legacyRequired) ?? fallbackMode);

        var username = dto.Username is not null ? dto.Username.Trim() : current.Username;
        var publicBaseUrl = dto.PublicBaseUrl is not null ? dto.PublicBaseUrl.Trim() : current.PublicBaseUrl;
        var (auth, authRequired) = LegacyAuthProjection(authMode);

        var next = new SecuritySettings
        {
            AuthMode = authMode,
            Authentication = auth,
            AuthenticationRequired = authRequired,
            PublicBaseUrl = publicBaseUrl ?? "",
            Username = username ?? "",
            PasswordHash = current.PasswordHash,
            PasswordSalt = current.PasswordSalt
        };

        var hasPasswordUpdate = !string.IsNullOrWhiteSpace(dto.Password) || !string.IsNullOrWhiteSpace(dto.PasswordConfirmation);
        if (hasPasswordUpdate)
        {
            if (string.IsNullOrWhiteSpace(dto.Password) || string.IsNullOrWhiteSpace(dto.PasswordConfirmation))
                return Problem(title: "password and confirmation required", statusCode: StatusCodes.Status400BadRequest);

            if (!string.Equals(dto.Password, dto.PasswordConfirmation, StringComparison.Ordinal))
                return Problem(title: "password confirmation mismatch", statusCode: StatusCodes.Status400BadRequest);

            if (!TryValidatePasswordStrength(dto.Password, out var passwordError))
                return Problem(title: passwordError, statusCode: StatusCodes.Status400BadRequest);

            var (hash, salt) = HashPassword(dto.Password);
            next.PasswordHash = hash;
            next.PasswordSalt = salt;
        }

        var bootstrapSecret = SmartAuthPolicy.GetBootstrapSecret(_config);
        if (authMode is "smart" or "strict")
        {
            var exposed = SmartAuthPolicy.IsExposedRequest(HttpContext, next);
            var authConfiguredCandidate = SmartAuthPolicy.IsAuthConfigured(next, bootstrapSecret);
            if (exposed && !authConfiguredCandidate)
            {
                return BadRequest(new
                {
                    error = "credentials_required",
                    message = "Credentials are required when AuthMode is smart/strict and instance is exposed. Set username/password or switch to open."
                });
            }
        }

        _repo.SaveSecurity(next);
        _cache.Remove(SecuritySettingsCache.CacheKey);
        var authConfigured = SmartAuthPolicy.IsAuthConfigured(next, bootstrapSecret);
        var effectiveAuthRequired = SmartAuthPolicy.IsAuthRequired(HttpContext, next);
        return Ok(new
        {
            authMode = next.AuthMode,
            authentication = next.Authentication,
            authenticationRequired = next.AuthenticationRequired,
            publicBaseUrl = next.PublicBaseUrl,
            username = next.Username,
            hasPassword = !string.IsNullOrWhiteSpace(next.PasswordHash),
            authConfigured,
            authRequired = effectiveAuthRequired
        });
    }

    private static string Normalize(string? value, string fallback, IEnumerable<string> allowed)
    {
        var v = (value ?? fallback).Trim().ToLowerInvariant();
        return allowed.Contains(v) ? v : fallback;
    }

    private static (string authentication, string authenticationRequired) LegacyAuthProjection(string authMode)
    {
        return authMode switch
        {
            "open" => ("none", "local"),
            "strict" => ("basic", "all"),
            _ => ("basic", "local")
        };
    }

    private static string? LegacyModeProjection(string authentication, string authenticationRequired)
    {
        if (authentication == "none")
            return "open";
        if (authentication == "basic" && authenticationRequired == "all")
            return "strict";
        if (authentication == "basic")
            return "smart";
        return null;
    }

    private static (string hash, string salt) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
        var hash = pbkdf2.GetBytes(32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    private static bool TryValidatePasswordStrength(string password, out string error)
    {
        const int minLength = 12;
        error = "";

        if (password.Length < minLength)
        {
            error = $"password must contain at least {minLength} characters";
            return false;
        }

        var hasLower = password.Any(char.IsLower);
        var hasUpper = password.Any(char.IsUpper);
        var hasDigit = password.Any(char.IsDigit);
        var hasSymbol = password.Any(ch => !char.IsLetterOrDigit(ch));

        if (!hasLower || !hasUpper || !hasDigit || !hasSymbol)
        {
            error = "password must include uppercase, lowercase, digit and symbol";
            return false;
        }

        return true;
    }
}
