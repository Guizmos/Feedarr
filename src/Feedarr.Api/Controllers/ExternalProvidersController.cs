using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Dtos.Providers;
using Feedarr.Api.Models;
using Feedarr.Api.Services.ExternalProviders;
using Microsoft.AspNetCore.Mvc;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/providers/external")]
public sealed class ExternalProvidersController : ControllerBase
{
    private readonly ExternalProviderRegistry _registry;
    private readonly ExternalProviderInstanceRepository _instances;
    private readonly ExternalProviderTestService _testService;

    public ExternalProvidersController(
        ExternalProviderRegistry registry,
        ExternalProviderInstanceRepository instances,
        ExternalProviderTestService testService)
    {
        _registry = registry;
        _instances = instances;
        _testService = testService;
    }

    [HttpGet]
    public IActionResult Get()
    {
        var definitions = _registry.List().Select(MapDefinition).ToList();
        var definitionByKey = definitions.ToDictionary(d => d.ProviderKey, StringComparer.OrdinalIgnoreCase);

        var instances = _instances.List()
            .Select(instance =>
            {
                definitionByKey.TryGetValue(instance.ProviderKey, out var definition);
                return MapInstance(instance, definition);
            })
            .ToList();

        return Ok(new
        {
            definitions,
            instances
        });
    }

    [HttpPost]
    public IActionResult Create([FromBody] ExternalProviderCreateDto dto)
    {
        if (dto is null)
            return Problem(title: "body missing", statusCode: StatusCodes.Status400BadRequest);

        if (!_registry.TryGet(dto.ProviderKey, out var definition))
            return Problem(title: "provider key invalid", statusCode: StatusCodes.Status400BadRequest);

        if (!TryValidateBaseUrl(dto.BaseUrl, out var baseUrlError))
            return Problem(title: baseUrlError, statusCode: StatusCodes.Status400BadRequest);

        var missingRequiredFields = GetMissingRequiredFields(definition, dto.Auth, existingAuth: null);
        if (missingRequiredFields.Count > 0)
        {
            return Problem(
                title: $"missing required auth field(s): {string.Join(", ", missingRequiredFields)}",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var created = _instances.Create(dto);
        return Ok(MapInstance(created, MapDefinition(definition)));
    }

    [HttpPut("{instanceId}")]
    public IActionResult Update([FromRoute] string instanceId, [FromBody] ExternalProviderUpdateDto dto)
    {
        if (dto is null)
            return Problem(title: "body missing", statusCode: StatusCodes.Status400BadRequest);

        if (!TryValidateBaseUrl(dto.BaseUrl, out var baseUrlError))
            return Problem(title: baseUrlError, statusCode: StatusCodes.Status400BadRequest);

        var current = _instances.GetWithSecrets(instanceId);
        if (current is null)
            return Problem(title: "instance not found", statusCode: StatusCodes.Status404NotFound);

        if (!_registry.TryGet(current.ProviderKey, out var definition))
            return Problem(title: "provider key invalid", statusCode: StatusCodes.Status400BadRequest);

        var missingRequiredFields = GetMissingRequiredFields(definition, dto.Auth, current.Auth);
        if (missingRequiredFields.Count > 0)
        {
            return Problem(
                title: $"missing required auth field(s): {string.Join(", ", missingRequiredFields)}",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var updated = _instances.Update(instanceId, dto);
        if (updated is null)
            return Problem(title: "instance not found", statusCode: StatusCodes.Status404NotFound);

        return Ok(MapInstance(updated, MapDefinition(definition)));
    }

    [HttpDelete("{instanceId}")]
    public IActionResult Delete([FromRoute] string instanceId)
    {
        var deleted = _instances.Delete(instanceId);
        if (!deleted)
            return Problem(title: "instance not found", statusCode: StatusCodes.Status404NotFound);

        return Ok(new { ok = true, instanceId });
    }

    [HttpPost("{instanceId}/test")]
    public async Task<IActionResult> Test([FromRoute] string instanceId, CancellationToken ct)
    {
        var instance = _instances.GetWithSecrets(instanceId);
        if (instance is null)
            return Problem(title: "instance not found", statusCode: StatusCodes.Status404NotFound);

        if (!_registry.TryGet(instance.ProviderKey, out _))
            return Problem(title: "provider key invalid", statusCode: StatusCodes.Status400BadRequest);

        var outcome = await _testService.TestAsync(
            instance.ProviderKey,
            instance.BaseUrl,
            instance.Auth,
            ct);

        return Ok(new ExternalProviderTestResultDto
        {
            Ok = outcome.Ok,
            ElapsedMs = outcome.ElapsedMs,
            Error = outcome.Error
        });
    }

    private static ExternalProviderDefinitionDto MapDefinition(ExternalProviderDefinition definition)
    {
        return new ExternalProviderDefinitionDto
        {
            ProviderKey = definition.ProviderKey,
            DisplayName = definition.DisplayName,
            Kind = definition.Kind,
            DefaultBaseUrl = definition.DefaultBaseUrl,
            UiHints = new ExternalProviderUiHintsDto
            {
                Icon = definition.UiHints.Icon,
                Badges = definition.UiHints.Badges.ToArray()
            },
            FieldsSchema = definition.FieldsSchema
                .Select(field => new ExternalProviderFieldSchemaDto
                {
                    Key = field.Key,
                    Label = field.Label,
                    Type = field.Type,
                    Placeholder = field.Placeholder,
                    Required = field.Required,
                    Secret = field.Secret,
                    SecretPlaceholder = field.SecretPlaceholder
                })
                .ToArray()
        };
    }

    private static ExternalProviderInstanceDto MapInstance(
        ExternalProviderInstance instance,
        ExternalProviderDefinitionDto? definition)
    {
        var dto = new ExternalProviderInstanceDto
        {
            InstanceId = instance.InstanceId,
            ProviderKey = instance.ProviderKey,
            DisplayName = instance.DisplayName,
            Enabled = instance.Enabled,
            BaseUrl = instance.BaseUrl,
            Options = instance.Options,
            CreatedAtTs = instance.CreatedAtTs,
            UpdatedAtTs = instance.UpdatedAtTs
        };

        if (definition?.FieldsSchema is not null)
        {
            foreach (var field in definition.FieldsSchema)
            {
                var hasValue = instance.Auth.TryGetValue(field.Key, out var value)
                    && !string.IsNullOrWhiteSpace(value);
                dto.AuthFlags[$"has{UppercaseFirst(field.Key)}"] = hasValue;
            }
        }

        return dto;
    }

    private static string UppercaseFirst(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var trimmed = value.Trim();
        return trimmed.Length == 1
            ? trimmed.ToUpperInvariant()
            : char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    private static bool TryValidateBaseUrl(string? baseUrl, out string error)
    {
        error = "";
        if (baseUrl is null)
            return true;

        var trimmed = baseUrl.Trim();
        if (trimmed.Length == 0)
            return true;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out _))
        {
            error = "baseUrl invalid";
            return false;
        }

        return true;
    }

    private static List<string> GetMissingRequiredFields(
        ExternalProviderDefinition definition,
        Dictionary<string, string?>? requestedAuth,
        Dictionary<string, string?>? existingAuth)
    {
        var missing = new List<string>();
        foreach (var field in definition.FieldsSchema.Where(f => f.Required))
        {
            var hasRequested = requestedAuth is not null
                && requestedAuth.TryGetValue(field.Key, out var requestedValue)
                && !string.IsNullOrWhiteSpace(requestedValue?.Trim());

            var hasExisting = existingAuth is not null
                && existingAuth.TryGetValue(field.Key, out var existingValue)
                && !string.IsNullOrWhiteSpace(existingValue?.Trim());

            if (!hasRequested && !hasExisting)
                missing.Add(field.Key);
        }

        return missing;
    }
}
