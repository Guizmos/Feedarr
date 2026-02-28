using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;

namespace Feedarr.Api.Services.ExternalProviders;

public sealed record ActiveExternalProviderConfig(
    string ProviderKey,
    bool Enabled,
    string? BaseUrl,
    Dictionary<string, string?> Auth,
    string Source);

public sealed class ActiveExternalProviderConfigResolver
{
    private readonly ExternalProviderInstanceRepository _instances;
    private readonly ExternalProviderRegistry _registry;
    private readonly ILogger<ActiveExternalProviderConfigResolver> _logger;

    public ActiveExternalProviderConfigResolver(
        ExternalProviderInstanceRepository instances,
        ExternalProviderRegistry registry,
        ILogger<ActiveExternalProviderConfigResolver> logger)
    {
        _instances = instances;
        _registry = registry;
        _logger = logger;
    }

    public ActiveExternalProviderConfig Resolve(string providerKey)
    {
        var normalizedProviderKey = NormalizeProviderKey(providerKey);
        if (string.IsNullOrWhiteSpace(normalizedProviderKey))
        {
            return BuildSafeDisabledConfig("");
        }

        if (!_registry.TryGet(normalizedProviderKey, out var definition))
        {
            _logger.LogDebug("Unknown external provider key '{ProviderKey}'.", normalizedProviderKey);
            return BuildSafeDisabledConfig(normalizedProviderKey);
        }

        try
        {
            var active = _instances.GetActiveByProviderKeyWithSecrets(normalizedProviderKey);

            if (active is not null)
            {
                LogMissingRequiredCredentials(normalizedProviderKey, definition, active.Auth, active.Enabled);

                return new ActiveExternalProviderConfig(
                    ProviderKey: normalizedProviderKey,
                    Enabled: active.Enabled,
                    BaseUrl: active.BaseUrl,
                    Auth: CloneAuth(active.Auth),
                    Source: "instances");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed resolving active external provider config for key '{ProviderKey}'.", normalizedProviderKey);
        }

        return BuildSafeDisabledConfig(normalizedProviderKey);
    }

    public bool GetActiveEnabled(string providerKey)
        => Resolve(providerKey).Enabled;

    public IReadOnlyDictionary<string, string?> GetActiveAuth(string providerKey)
        => Resolve(providerKey).Auth;

    private void LogMissingRequiredCredentials(
        string providerKey,
        ExternalProviderDefinition definition,
        IReadOnlyDictionary<string, string?> auth,
        bool enabled)
    {
        if (!enabled)
            return;

        foreach (var field in definition.FieldsSchema.Where(f => f.Required))
        {
            if (auth.TryGetValue(field.Key, out var value) && !string.IsNullOrWhiteSpace(value))
                continue;

            _logger.LogDebug(
                "Provider '{ProviderKey}' enabled but missing credential '{CredentialField}'.",
                providerKey,
                field.Key);
        }
    }

    private static ActiveExternalProviderConfig BuildSafeDisabledConfig(string providerKey)
    {
        return new ActiveExternalProviderConfig(
            ProviderKey: providerKey,
            Enabled: false,
            BaseUrl: null,
            Auth: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
            Source: "none");
    }

    private static string NormalizeProviderKey(string? providerKey)
    {
        return (providerKey ?? "").Trim().ToLowerInvariant();
    }

    private static Dictionary<string, string?> CloneAuth(Dictionary<string, string?> source)
    {
        return source is null
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(source, StringComparer.OrdinalIgnoreCase);
    }
}
