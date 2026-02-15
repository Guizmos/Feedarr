using System.Security.Cryptography;
using System.Text;
using System.Linq;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Dtos.Providers;
using Feedarr.Api.Services.Jackett;
using Feedarr.Api.Services.Prowlarr;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/providers")]
public sealed class ProvidersController : ControllerBase
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
    private readonly ProviderRepository _providers;
    private readonly SourceRepository _sources;
    private readonly JackettClient _jackett;
    private readonly ProwlarrClient _prowlarr;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ProvidersController> _log;

    public ProvidersController(
        ProviderRepository providers,
        SourceRepository sources,
        JackettClient jackett,
        ProwlarrClient prowlarr,
        IMemoryCache cache,
        ILogger<ProvidersController> log)
    {
        _providers = providers;
        _sources = sources;
        _jackett = jackett;
        _prowlarr = prowlarr;
        _cache = cache;
        _log = log;
    }

    [HttpGet]
    public IActionResult List()
    {
        _providers.BootstrapFromSources();
        var list = _providers.List().ToList();

        var rows = new List<object>();
        foreach (var p in list)
        {
            rows.Add(new
            {
                id = (long)p.Id,
                type = Convert.ToString(p.Type) ?? "",
                name = Convert.ToString(p.Name) ?? "",
                baseUrl = Convert.ToString(p.BaseUrl) ?? "",
                enabled = Convert.ToInt64(p.Enabled) == 1,
                lastTestOkAt = (long?)p.LastTestOkAt,
                hasApiKey = Convert.ToInt64(p.HasApiKey) == 1,
                linkedSources = Convert.ToInt64(p.LinkedSources)
            });
        }

        return Ok(rows);
    }

    [HttpPost]
    public IActionResult Create([FromBody] ProviderCreateDto dto)
    {
        var type = NormalizeType(dto.Type);
        if (type is null)
            return Problem(title: "provider type invalid", statusCode: StatusCodes.Status400BadRequest);

        if (!OutboundUrlGuard.TryNormalizeProviderBaseUrl(type, dto.BaseUrl, out var baseUrl, out var baseUrlError))
            return Problem(title: baseUrlError, statusCode: StatusCodes.Status400BadRequest);

        var apiKey = (dto.ApiKey ?? "").Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
            return Problem(title: "apiKey missing", statusCode: StatusCodes.Status400BadRequest);

        var name = string.IsNullOrWhiteSpace(dto.Name) ? DefaultName(type) : dto.Name!.Trim();
        var enabled = dto.Enabled ?? true;

        var id = _providers.Create(type, name, baseUrl, apiKey, enabled);
        InvalidateIndexersCache(id);
        return Ok(new { id });
    }

    [HttpPost("test")]
    public async Task<IActionResult> TestInline([FromBody] ProviderTestRequestDto dto, CancellationToken ct)
    {
        var type = NormalizeType(dto.Type);
        if (type is null)
            return Problem(title: "provider type invalid", statusCode: StatusCodes.Status400BadRequest);

        if (!OutboundUrlGuard.TryNormalizeProviderBaseUrl(type, dto.BaseUrl, out var baseUrl, out var baseUrlError))
            return Problem(title: baseUrlError, statusCode: StatusCodes.Status400BadRequest);

        var apiKey = (dto.ApiKey ?? "").Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
            return Problem(title: "apiKey missing", statusCode: StatusCodes.Status400BadRequest);

        try
        {
            List<(string id, string name, string torznabUrl)> list;
            if (type.Equals("prowlarr", StringComparison.OrdinalIgnoreCase))
            {
                list = await _prowlarr.ListIndexersAsync(baseUrl, apiKey, ct);
            }
            else if (type.Equals("jackett", StringComparison.OrdinalIgnoreCase))
            {
                list = await _jackett.ListIndexersAsync(baseUrl, apiKey, ct);
            }
            else
            {
                return Problem(title: "provider type not supported", statusCode: StatusCodes.Status501NotImplemented);
            }
            return Ok(new { ok = true, count = list.Count });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Provider test failed (inline, type={ProviderType})", type);
            return Problem(title: "provider test failed", detail: "upstream provider unavailable", statusCode: StatusCodes.Status502BadGateway);
        }
    }

    [HttpPut("{id:long}")]
    public IActionResult Update([FromRoute] long id, [FromBody] ProviderUpdateDto dto)
    {
        var current = _providers.Get(id);
        if (current is null)
            return Problem(title: "provider not found", statusCode: StatusCodes.Status404NotFound);

        var type = NormalizeType(dto.Type ?? current.Type ?? "");
        if (type is null)
            return Problem(title: "provider type invalid", statusCode: StatusCodes.Status400BadRequest);

        var name = string.IsNullOrWhiteSpace(dto.Name) ? current.Name ?? DefaultName(type) : dto.Name!.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = DefaultName(type);

        var rawBaseUrl = string.IsNullOrWhiteSpace(dto.BaseUrl)
            ? current.BaseUrl ?? ""
            : dto.BaseUrl!;
        if (!OutboundUrlGuard.TryNormalizeProviderBaseUrl(type, rawBaseUrl, out var baseUrl, out var baseUrlError))
            return Problem(title: baseUrlError, statusCode: StatusCodes.Status400BadRequest);

        string? apiKeyMaybe = null;
        if (!string.IsNullOrWhiteSpace(dto.ApiKey))
            apiKeyMaybe = dto.ApiKey.Trim();

        var ok = _providers.Update(id, type, name, baseUrl, apiKeyMaybe);
        if (!ok)
            return Problem(title: "provider not found", statusCode: StatusCodes.Status404NotFound);

        InvalidateIndexersCache(id);
        return Ok(new { id, type, name, baseUrl });
    }

    [HttpDelete("{id:long}")]
    public IActionResult Delete([FromRoute] long id)
    {
        var current = _providers.Get(id);
        if (current is null)
            return Problem(title: "provider not found", statusCode: StatusCodes.Status404NotFound);

        var linked = _sources.CountByProviderId(id);
        if (linked > 0)
        {
            _sources.DisableByProviderId(id);
        }

        var rows = _providers.Delete(id);
        if (rows == 0)
            return Problem(title: "provider not found", statusCode: StatusCodes.Status404NotFound);

        InvalidateIndexersCache(id);
        return Ok(new { id, linkedSources = linked, disabledSources = linked });
    }

    [HttpPut("{id:long}/enabled")]
    public IActionResult SetEnabled([FromRoute] long id, [FromBody] ProviderEnabledDto dto)
    {
        var ok = _providers.SetEnabled(id, dto.Enabled);
        if (!ok)
            return Problem(title: "provider not found", statusCode: StatusCodes.Status404NotFound);

        var disabledSources = 0;
        string? message = null;
        if (!dto.Enabled)
        {
            disabledSources = _sources.DisableByProviderId(id);
            if (disabledSources > 0)
                message = $"{disabledSources} indexeur{(disabledSources > 1 ? "s" : "")} désactivé{(disabledSources > 1 ? "s" : "")}.";
        }

        return Ok(new { id, enabled = dto.Enabled, disabledSources, message });
    }

    [HttpPost("{id:long}/test")]
    public async Task<IActionResult> Test([FromRoute] long id, CancellationToken ct)
    {
        var provider = _providers.Get(id);
        if (provider is null)
            return Problem(title: "provider not found", statusCode: StatusCodes.Status404NotFound);

        var type = provider.Type;
        var baseUrl = provider.BaseUrl;
        var apiKey = provider.ApiKey ?? "";

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
            return Problem(title: "provider configuration incomplete", statusCode: StatusCodes.Status400BadRequest);
        if (!OutboundUrlGuard.TryNormalizeProviderBaseUrl(type, baseUrl, out var normalizedBaseUrl, out _))
            return Problem(title: "provider baseUrl invalid", statusCode: StatusCodes.Status400BadRequest);

        try
        {
            List<(string id, string name, string torznabUrl)> list;
            if (type.Equals("prowlarr", StringComparison.OrdinalIgnoreCase))
            {
                list = await _prowlarr.ListIndexersAsync(normalizedBaseUrl, apiKey, ct);
            }
            else if (type.Equals("jackett", StringComparison.OrdinalIgnoreCase))
            {
                list = await _jackett.ListIndexersAsync(normalizedBaseUrl, apiKey, ct);
            }
            else
            {
                return Problem(title: "provider type not supported", statusCode: StatusCodes.Status501NotImplemented);
            }
            _providers.UpdateLastTestOk(id);
            return Ok(new { ok = true, count = list.Count });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Provider test failed: {Id} (type={ProviderType})", id, type);
            return Problem(title: "provider test failed", detail: "upstream provider unavailable", statusCode: StatusCodes.Status502BadGateway);
        }
    }

    [HttpGet("{id:long}/indexers")]
    public async Task<IActionResult> ListIndexers([FromRoute] long id, CancellationToken ct)
    {
        var provider = _providers.Get(id);
        if (provider is null)
            return Problem(title: "provider not found", statusCode: StatusCodes.Status404NotFound);

        var type = provider.Type;
        var baseUrl = provider.BaseUrl;
        var apiKey = provider.ApiKey ?? "";

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
            return Problem(title: "provider configuration incomplete", statusCode: StatusCodes.Status400BadRequest);
        if (!OutboundUrlGuard.TryNormalizeProviderBaseUrl(type, baseUrl, out var normalizedBaseUrl, out _))
            return Problem(title: "provider baseUrl invalid", statusCode: StatusCodes.Status400BadRequest);

        if (!type.Equals("jackett", StringComparison.OrdinalIgnoreCase) &&
            !type.Equals("prowlarr", StringComparison.OrdinalIgnoreCase))
            return Problem(title: "provider type not supported", statusCode: StatusCodes.Status501NotImplemented);

        try
        {
            List<(string id, string name, string torznabUrl)> list = await GetCachedIndexersAsync(id, type, normalizedBaseUrl, apiKey, ct);
            var result = new List<object>(list.Count);
            foreach (var x in list)
            {
                result.Add(new { id = x.id, name = x.name, torznabUrl = x.torznabUrl });
            }
            return Ok(result);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Provider indexers fetch failed: {Id} (type={ProviderType})", id, type);
            return Problem(title: "provider indexers fetch failed", detail: "upstream provider unavailable", statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private async Task<List<(string id, string name, string torznabUrl)>> GetCachedIndexersAsync(
        long providerId,
        string providerType,
        string baseUrl,
        string apiKey,
        CancellationToken ct)
    {
        var fingerprint = BuildFingerprint(providerId, baseUrl, apiKey);
        var cacheKey = BuildCacheKey(providerId);
        if (_cache.TryGetValue(cacheKey, out CacheEntry? cached) && cached is not null)
        {
            if (string.Equals(cached.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                return cached.Indexers;
        }

        List<(string id, string name, string torznabUrl)> list;
        if (providerType.Equals("prowlarr", StringComparison.OrdinalIgnoreCase))
        {
            list = await _prowlarr.ListIndexersAsync(baseUrl, apiKey, ct);
        }
        else
        {
            list = await _jackett.ListIndexersAsync(baseUrl, apiKey, ct);
        }
        _cache.Set(cacheKey, new CacheEntry(fingerprint, list), CacheTtl);
        return list;
    }

    private void InvalidateIndexersCache(long providerId)
    {
        _cache.Remove(BuildCacheKey(providerId));
    }

    private static string BuildCacheKey(long providerId) => $"provider:indexers:{providerId}";

    private static string BuildFingerprint(long providerId, string baseUrl, string apiKey)
    {
        var raw = $"{providerId}|{baseUrl}|{apiKey}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? NormalizeType(string? value)
    {
        var type = (value ?? "").Trim().ToLowerInvariant();
        if (type == "jackett" || type == "prowlarr") return type;
        return null;
    }

    private static string DefaultName(string type)
        => type.Equals("prowlarr", StringComparison.OrdinalIgnoreCase) ? "Prowlarr" : "Jackett";

    private sealed record CacheEntry(string Fingerprint, List<(string id, string name, string torznabUrl)> Indexers);
}
