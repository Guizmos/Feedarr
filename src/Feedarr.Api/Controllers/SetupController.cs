using System;
using System.Linq;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/setup")]
public sealed class SetupController : ControllerBase
{
    private readonly Db _db;
    private readonly SettingsRepository _settings;
    private readonly ProviderRepository _providers;
    private readonly ILogger<SetupController> _log;

    public SetupController(
        Db db,
        SettingsRepository settings,
        ProviderRepository providers,
        ILogger<SetupController> log)
    {
        _db = db;
        _settings = settings;
        _providers = providers;
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

        return Ok(new
        {
            onboardingDone = ui.OnboardingDone,
            hasSources,
            hasJackettSource,
            hasProwlarrSource
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

        var normalizedBaseUrl = NormalizeBaseUrl(normalizedType, dto.BaseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            return Problem(title: "baseUrl missing", statusCode: StatusCodes.Status400BadRequest);

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

    private static string NormalizeBaseUrl(string type, string baseUrl)
    {
        var trimmed = (baseUrl ?? "").Trim().TrimEnd('/');
        if (type.Equals("jackett", StringComparison.OrdinalIgnoreCase) &&
            trimmed.EndsWith("/api/v2.0", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^"/api/v2.0".Length];
        }
        if (type.Equals("prowlarr", StringComparison.OrdinalIgnoreCase) &&
            trimmed.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^"/api/v1".Length];
        }
        return trimmed;
    }
}
