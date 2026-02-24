using System.Text.Json;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Dtos.Providers;
using Feedarr.Api.Models;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Data.Repositories;

public sealed class ExternalProviderInstanceRepository
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly Db _db;
    private readonly SettingsRepository _settingsRepository;
    private readonly IApiKeyProtectionService _keyProtection;
    private readonly ExternalProviderRegistry _registry;
    private readonly ILogger<ExternalProviderInstanceRepository> _logger;
    private readonly SemaphoreSlim _legacyProjectionLock = new(1, 1);
    private readonly SemaphoreSlim _seedFreeLock = new(1, 1);

    public ExternalProviderInstanceRepository(
        Db db,
        SettingsRepository settingsRepository,
        IApiKeyProtectionService keyProtection,
        ExternalProviderRegistry registry,
        ILogger<ExternalProviderInstanceRepository> logger)
    {
        _db = db;
        _settingsRepository = settingsRepository;
        _keyProtection = keyProtection;
        _registry = registry;
        _logger = logger;
    }

    public IEnumerable<ExternalProviderInstance> List()
    {
        using var conn = _db.Open();
        var rows = conn.Query<ExternalProviderInstanceRow>(
            """
            SELECT
              instance_id AS InstanceId,
              provider_key AS ProviderKey,
              display_name AS DisplayName,
              enabled AS Enabled,
              base_url AS BaseUrl,
              auth_json AS AuthJson,
              options_json AS OptionsJson,
              created_at_ts AS CreatedAtTs,
              updated_at_ts AS UpdatedAtTs
            FROM external_provider_instances
            ORDER BY updated_at_ts DESC, created_at_ts DESC;
            """
        );
        return rows.Select(row => MapRow(row, includeSecrets: false)).ToList();
    }

    public ExternalProviderInstance? Get(string instanceId)
    {
        var row = GetRow(instanceId);
        return row is null ? null : MapRow(row, includeSecrets: false);
    }

    public ExternalProviderInstance? GetWithSecrets(string instanceId)
    {
        var row = GetRow(instanceId);
        return row is null ? null : MapRow(row, includeSecrets: true);
    }

    public ExternalProviderInstance Create(ExternalProviderCreateDto dto)
    {
        if (!_registry.TryGet(dto.ProviderKey, out var definition))
            throw new InvalidOperationException($"Unknown external provider key: {dto.ProviderKey}");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var instanceId = Guid.NewGuid().ToString();
        var auth = PrepareAuthForPersist(
            definition,
            requestedAuth: dto.Auth,
            existingAuth: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
            isCreate: true);
        var options = NormalizeOptions(dto.Options);

        using var conn = _db.Open();
        conn.Execute(
            """
            INSERT INTO external_provider_instances(
              instance_id,
              provider_key,
              display_name,
              enabled,
              base_url,
              auth_json,
              options_json,
              created_at_ts,
              updated_at_ts
            )
            VALUES (
              @instanceId,
              @providerKey,
              @displayName,
              @enabled,
              @baseUrl,
              @authJson,
              @optionsJson,
              @now,
              @now
            );
            """,
            new
            {
                instanceId,
                providerKey = definition.ProviderKey,
                displayName = NormalizeOptionalString(dto.DisplayName),
                enabled = dto.Enabled != false ? 1 : 0,
                baseUrl = NormalizeOptionalString(dto.BaseUrl),
                authJson = SerializeAuth(auth),
                optionsJson = SerializeOptions(options),
                now
            }
        );

        SyncLegacyExternalSettingsFromInstances();
        return Get(instanceId)
            ?? throw new InvalidOperationException($"Failed to load created external provider instance: {instanceId}");
    }

    public ExternalProviderInstance? Update(string instanceId, ExternalProviderUpdateDto dto)
    {
        var row = GetRow(instanceId);
        if (row is null)
            return null;

        if (!_registry.TryGet(row.ProviderKey, out var definition))
            throw new InvalidOperationException($"Unknown external provider key: {row.ProviderKey}");

        var currentAuth = ParseAuthJson(row.AuthJson);
        var nextAuth = PrepareAuthForPersist(
            definition,
            requestedAuth: dto.Auth,
            existingAuth: currentAuth,
            isCreate: false);

        var currentOptions = ParseOptionsJson(row.OptionsJson);
        var nextOptions = dto.Options is null
            ? currentOptions
            : NormalizeOptions(dto.Options);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nextDisplayName = dto.DisplayName is null
            ? row.DisplayName
            : NormalizeOptionalString(dto.DisplayName);
        var nextBaseUrl = dto.BaseUrl is null
            ? row.BaseUrl
            : NormalizeOptionalString(dto.BaseUrl);
        var nextEnabled = dto.Enabled ?? row.Enabled;

        using var conn = _db.Open();
        var updated = conn.Execute(
            """
            UPDATE external_provider_instances
            SET
              display_name = @displayName,
              enabled = @enabled,
              base_url = @baseUrl,
              auth_json = @authJson,
              options_json = @optionsJson,
              updated_at_ts = @updatedAtTs
            WHERE instance_id = @instanceId;
            """,
            new
            {
                instanceId,
                displayName = nextDisplayName,
                enabled = nextEnabled ? 1 : 0,
                baseUrl = nextBaseUrl,
                authJson = SerializeAuth(nextAuth),
                optionsJson = SerializeOptions(nextOptions),
                updatedAtTs = now
            }
        );

        if (updated == 0)
            return null;

        SyncLegacyExternalSettingsFromInstances();
        return Get(instanceId);
    }

    public bool Delete(string instanceId)
    {
        using var conn = _db.Open();
        var deleted = conn.Execute(
            "DELETE FROM external_provider_instances WHERE instance_id = @instanceId",
            new { instanceId }
        );

        if (deleted <= 0)
            return false;

        SyncLegacyExternalSettingsFromInstances();
        return true;
    }

    public void UpsertFromLegacyDefaults()
    {
        _legacyProjectionLock.Wait();
        try
        {
            var legacy = _settingsRepository.GetExternal(new ExternalSettings
            {
                TmdbEnabled = false,
                TvmazeEnabled = false,
                FanartEnabled = false,
                IgdbEnabled = false
            });

            using var conn = _db.Open();
            using var tx = conn.BeginTransaction();

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var inserted = 0;

            foreach (var providerKey in new[] { ExternalProviderKeys.Tmdb, ExternalProviderKeys.Tvmaze, ExternalProviderKeys.Fanart, ExternalProviderKeys.Igdb })
            {
                if (!_registry.TryGet(providerKey, out var definition))
                    continue;

                var exists = conn.ExecuteScalar<long>(
                    """
                    SELECT COUNT(1)
                    FROM external_provider_instances
                    WHERE LOWER(provider_key) = LOWER(@providerKey);
                    """,
                    new { providerKey },
                    tx);

                if (exists > 0)
                    continue;

                if (!ShouldCreateFromLegacy(providerKey, legacy))
                    continue;

                var auth = BuildLegacyAuth(providerKey, legacy);
                var persistedAuth = PrepareAuthForPersist(
                    definition,
                    requestedAuth: auth,
                    existingAuth: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
                    isCreate: true);

                conn.Execute(
                    """
                    INSERT INTO external_provider_instances(
                      instance_id,
                      provider_key,
                      display_name,
                      enabled,
                      base_url,
                      auth_json,
                      options_json,
                      created_at_ts,
                      updated_at_ts
                    )
                    VALUES(
                      @instanceId,
                      @providerKey,
                      NULL,
                      @enabled,
                      NULL,
                      @authJson,
                      '{}',
                      @now,
                      @now
                    );
                    """,
                    new
                    {
                        instanceId = Guid.NewGuid().ToString(),
                        providerKey,
                        enabled = GetLegacyEnabled(providerKey, legacy) ? 1 : 0,
                        authJson = SerializeAuth(persistedAuth),
                        now
                    },
                    tx
                );
                inserted++;
            }

            tx.Commit();

            if (inserted > 0)
                SyncLegacyExternalSettingsFromInstances();
        }
        finally
        {
            _legacyProjectionLock.Release();
        }
    }

    /// <summary>
    /// Auto-creates instances for providers that require no API key (or have a known public key).
    /// Idempotent — only inserts if no instance already exists for that provider key.
    /// </summary>
    public void SeedFreeProviders()
    {
        _seedFreeLock.Wait();
        try
        {
            using var conn = _db.Open();
            using var tx = conn.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Providers seeded automatically: key → default auth (empty = no auth needed)
            var freeProviders = new (string Key, Dictionary<string, string?> Auth)[]
            {
                (ExternalProviderKeys.Tvmaze,     new(StringComparer.OrdinalIgnoreCase)),
                (ExternalProviderKeys.Jikan,      new(StringComparer.OrdinalIgnoreCase)),
                (ExternalProviderKeys.MusicBrainz, new(StringComparer.OrdinalIgnoreCase)),
                (ExternalProviderKeys.GoogleBooks,  new(StringComparer.OrdinalIgnoreCase)),
                (ExternalProviderKeys.OpenLibrary,  new(StringComparer.OrdinalIgnoreCase)),
                (ExternalProviderKeys.TheAudioDb,   new(StringComparer.OrdinalIgnoreCase) { ["apiKey"] = "123" }),
            };

            foreach (var (providerKey, defaultAuth) in freeProviders)
            {
                if (!_registry.TryGet(providerKey, out var definition))
                    continue;

                var exists = conn.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM external_provider_instances WHERE LOWER(provider_key) = LOWER(@providerKey);",
                    new { providerKey }, tx);

                if (exists > 0)
                    continue;

                var auth = PrepareAuthForPersist(
                    definition,
                    requestedAuth: defaultAuth,
                    existingAuth: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
                    isCreate: true);

                conn.Execute(
                    """
                    INSERT INTO external_provider_instances(
                      instance_id, provider_key, display_name, enabled,
                      base_url, auth_json, options_json, created_at_ts, updated_at_ts
                    )
                    VALUES (@instanceId, @providerKey, NULL, 1, NULL, @authJson, '{}', @now, @now);
                    """,
                    new
                    {
                        instanceId = Guid.NewGuid().ToString(),
                        providerKey,
                        authJson = SerializeAuth(auth),
                        now
                    },
                    tx);
            }

            tx.Commit();
        }
        finally
        {
            _seedFreeLock.Release();
        }
    }

    public void UpsertFromLegacySettings(ExternalSettings legacy)
    {
        if (legacy is null)
            return;

        _legacyProjectionLock.Wait();
        try
        {
            using var conn = _db.Open();
            using var tx = conn.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var providerKey in new[] { ExternalProviderKeys.Tmdb, ExternalProviderKeys.Tvmaze, ExternalProviderKeys.Fanart, ExternalProviderKeys.Igdb })
            {
                if (!_registry.TryGet(providerKey, out var definition))
                    continue;

                var active = conn.QueryFirstOrDefault<ExternalProviderInstanceRow>(
                    """
                    SELECT
                      instance_id AS InstanceId,
                      provider_key AS ProviderKey,
                      display_name AS DisplayName,
                      enabled AS Enabled,
                      base_url AS BaseUrl,
                      auth_json AS AuthJson,
                      options_json AS OptionsJson,
                      created_at_ts AS CreatedAtTs,
                      updated_at_ts AS UpdatedAtTs
                    FROM external_provider_instances
                    WHERE LOWER(provider_key) = LOWER(@providerKey)
                    ORDER BY enabled DESC, updated_at_ts DESC, created_at_ts DESC
                    LIMIT 1;
                    """,
                    new { providerKey },
                    tx);

                var nextAuth = BuildLegacyAuth(providerKey, legacy);
                var persistedAuth = PrepareAuthForPersist(
                    definition,
                    requestedAuth: nextAuth,
                    existingAuth: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
                    isCreate: true);
                var enabled = GetLegacyEnabled(providerKey, legacy) ? 1 : 0;

                if (active is null)
                {
                    if (!ShouldCreateFromLegacy(providerKey, legacy))
                        continue;

                    conn.Execute(
                        """
                        INSERT INTO external_provider_instances(
                          instance_id,
                          provider_key,
                          display_name,
                          enabled,
                          base_url,
                          auth_json,
                          options_json,
                          created_at_ts,
                          updated_at_ts
                        )
                        VALUES(
                          @instanceId,
                          @providerKey,
                          NULL,
                          @enabled,
                          NULL,
                          @authJson,
                          '{}',
                          @now,
                          @now
                        );
                        """,
                        new
                        {
                            instanceId = Guid.NewGuid().ToString(),
                            providerKey,
                            enabled,
                            authJson = SerializeAuth(persistedAuth),
                            now
                        },
                        tx);

                    continue;
                }

                conn.Execute(
                    """
                    UPDATE external_provider_instances
                    SET
                      enabled = @enabled,
                      auth_json = @authJson,
                      updated_at_ts = @updatedAtTs
                    WHERE instance_id = @instanceId;
                    """,
                    new
                    {
                        instanceId = active.InstanceId,
                        enabled,
                        authJson = SerializeAuth(persistedAuth),
                        updatedAtTs = now
                    },
                    tx);
            }

            tx.Commit();
        }
        finally
        {
            _legacyProjectionLock.Release();
        }
    }

    private void SyncLegacyExternalSettingsFromInstances()
    {
        try
        {
            using var conn = _db.Open();
            var rows = conn.Query<ExternalProviderInstanceRow>(
                """
                SELECT
                  instance_id AS InstanceId,
                  provider_key AS ProviderKey,
                  display_name AS DisplayName,
                  enabled AS Enabled,
                  base_url AS BaseUrl,
                  auth_json AS AuthJson,
                  options_json AS OptionsJson,
                  created_at_ts AS CreatedAtTs,
                  updated_at_ts AS UpdatedAtTs
                FROM external_provider_instances
                WHERE LOWER(provider_key) IN ('tmdb', 'tvmaze', 'fanart', 'igdb')
                ORDER BY enabled DESC, updated_at_ts DESC, created_at_ts DESC;
                """
            ).ToList();

            var tmdb = rows.FirstOrDefault(r => string.Equals(r.ProviderKey, ExternalProviderKeys.Tmdb, StringComparison.OrdinalIgnoreCase));
            var tvmaze = rows.FirstOrDefault(r => string.Equals(r.ProviderKey, ExternalProviderKeys.Tvmaze, StringComparison.OrdinalIgnoreCase));
            var fanart = rows.FirstOrDefault(r => string.Equals(r.ProviderKey, ExternalProviderKeys.Fanart, StringComparison.OrdinalIgnoreCase));
            var igdb = rows.FirstOrDefault(r => string.Equals(r.ProviderKey, ExternalProviderKeys.Igdb, StringComparison.OrdinalIgnoreCase));

            var tmdbAuth = ParseAuthJson(tmdb?.AuthJson);
            var tvmazeAuth = ParseAuthJson(tvmaze?.AuthJson);
            var fanartAuth = ParseAuthJson(fanart?.AuthJson);
            var igdbAuth = ParseAuthJson(igdb?.AuthJson);

            var legacy = new ExternalSettings
            {
                TmdbApiKey = UnprotectAuthValue(tmdbAuth, "apiKey") ?? "",
                TvmazeApiKey = UnprotectAuthValue(tvmazeAuth, "apiKey") ?? "",
                FanartApiKey = UnprotectAuthValue(fanartAuth, "apiKey") ?? "",
                IgdbClientId = UnprotectAuthValue(igdbAuth, "clientId") ?? "",
                IgdbClientSecret = UnprotectAuthValue(igdbAuth, "clientSecret") ?? "",
                TmdbEnabled = tmdb?.Enabled ?? false,
                TvmazeEnabled = tvmaze?.Enabled ?? false,
                FanartEnabled = fanart?.Enabled ?? false,
                IgdbEnabled = igdb?.Enabled ?? false,
            };

            _settingsRepository.SaveExternalPartial(legacy);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to synchronize external provider instances to legacy app_settings");
        }
    }

    private ExternalProviderInstanceRow? GetRow(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return null;

        using var conn = _db.Open();
        return conn.QueryFirstOrDefault<ExternalProviderInstanceRow>(
            """
            SELECT
              instance_id AS InstanceId,
              provider_key AS ProviderKey,
              display_name AS DisplayName,
              enabled AS Enabled,
              base_url AS BaseUrl,
              auth_json AS AuthJson,
              options_json AS OptionsJson,
              created_at_ts AS CreatedAtTs,
              updated_at_ts AS UpdatedAtTs
            FROM external_provider_instances
            WHERE instance_id = @instanceId;
            """,
            new { instanceId = instanceId.Trim() }
        );
    }

    private ExternalProviderInstance MapRow(ExternalProviderInstanceRow row, bool includeSecrets)
    {
        var auth = ParseAuthJson(row.AuthJson);
        if (includeSecrets)
        {
            foreach (var key in auth.Keys.ToList())
            {
                var value = auth[key];
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                auth[key] = _keyProtection.IsProtected(value)
                    ? _keyProtection.Unprotect(value)
                    : value;
            }
        }

        return new ExternalProviderInstance
        {
            InstanceId = row.InstanceId,
            ProviderKey = row.ProviderKey,
            DisplayName = row.DisplayName,
            Enabled = row.Enabled,
            BaseUrl = row.BaseUrl,
            Auth = auth,
            Options = ParseOptionsJson(row.OptionsJson),
            CreatedAtTs = row.CreatedAtTs,
            UpdatedAtTs = row.UpdatedAtTs,
        };
    }

    private Dictionary<string, string?> PrepareAuthForPersist(
        ExternalProviderDefinition definition,
        Dictionary<string, string?>? requestedAuth,
        Dictionary<string, string?> existingAuth,
        bool isCreate)
    {
        var result = new Dictionary<string, string?>(existingAuth, StringComparer.OrdinalIgnoreCase);
        if (requestedAuth is null)
            return result;

        foreach (var field in definition.FieldsSchema)
        {
            if (!requestedAuth.TryGetValue(field.Key, out var rawValue))
                continue;

            var trimmed = rawValue?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                if (isCreate || !field.Secret)
                    result[field.Key] = "";
                continue;
            }

            result[field.Key] = field.Secret
                ? ProtectIfNeeded(trimmed)
                : trimmed;
        }

        return result;
    }

    private string ProtectIfNeeded(string plainTextOrProtected)
    {
        return _keyProtection.IsProtected(plainTextOrProtected)
            ? plainTextOrProtected
            : _keyProtection.Protect(plainTextOrProtected);
    }

    private string? UnprotectAuthValue(Dictionary<string, string?> auth, string key)
    {
        if (!auth.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return null;

        return _keyProtection.IsProtected(value)
            ? _keyProtection.Unprotect(value)
            : value;
    }

    private static Dictionary<string, object?> NormalizeOptions(Dictionary<string, object?>? input)
    {
        if (input is null || input.Count == 0)
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        var output = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in input)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;
            output[key.Trim()] = value;
        }

        return output;
    }

    private static Dictionary<string, string?> ParseAuthJson(string? authJson)
    {
        if (string.IsNullOrWhiteSpace(authJson))
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string?>>(authJson, JsonOpts);
            return parsed is null
                ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string?>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, object?> ParseOptionsJson(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(optionsJson, JsonOpts);
            return parsed is null
                ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object?>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string SerializeAuth(Dictionary<string, string?> auth)
    {
        return JsonSerializer.Serialize(auth ?? new Dictionary<string, string?>(), JsonOpts);
    }

    private static string SerializeOptions(Dictionary<string, object?> options)
    {
        return JsonSerializer.Serialize(options ?? new Dictionary<string, object?>(), JsonOpts);
    }

    private static string? NormalizeOptionalString(string? value)
    {
        if (value is null)
            return null;

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static Dictionary<string, string?> BuildLegacyAuth(string providerKey, ExternalSettings legacy)
    {
        return providerKey.ToLowerInvariant() switch
        {
            ExternalProviderKeys.Tmdb => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["apiKey"] = legacy.TmdbApiKey ?? ""
            },
            ExternalProviderKeys.Tvmaze => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["apiKey"] = legacy.TvmazeApiKey ?? ""
            },
            ExternalProviderKeys.Fanart => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["apiKey"] = legacy.FanartApiKey ?? ""
            },
            ExternalProviderKeys.Igdb => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["clientId"] = legacy.IgdbClientId ?? "",
                ["clientSecret"] = legacy.IgdbClientSecret ?? ""
            },
            _ => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static bool GetLegacyEnabled(string providerKey, ExternalSettings legacy)
    {
        return providerKey.ToLowerInvariant() switch
        {
            ExternalProviderKeys.Tmdb => legacy.TmdbEnabled != false,
            ExternalProviderKeys.Tvmaze => legacy.TvmazeEnabled != false,
            ExternalProviderKeys.Fanart => legacy.FanartEnabled != false,
            ExternalProviderKeys.Igdb => legacy.IgdbEnabled != false,
            _ => false
        };
    }

    private static bool ShouldCreateFromLegacy(string providerKey, ExternalSettings legacy)
    {
        var enabled = GetLegacyEnabled(providerKey, legacy);
        var hasAuth = providerKey.ToLowerInvariant() switch
        {
            ExternalProviderKeys.Tmdb => !string.IsNullOrWhiteSpace(legacy.TmdbApiKey),
            ExternalProviderKeys.Tvmaze => !string.IsNullOrWhiteSpace(legacy.TvmazeApiKey),
            ExternalProviderKeys.Fanart => !string.IsNullOrWhiteSpace(legacy.FanartApiKey),
            ExternalProviderKeys.Igdb => !string.IsNullOrWhiteSpace(legacy.IgdbClientId)
                      && !string.IsNullOrWhiteSpace(legacy.IgdbClientSecret),
            _ => false
        };

        return enabled || hasAuth;
    }

    private sealed class ExternalProviderInstanceRow
    {
        public string InstanceId { get; set; } = "";
        public string ProviderKey { get; set; } = "";
        public string? DisplayName { get; set; }
        public bool Enabled { get; set; }
        public string? BaseUrl { get; set; }
        public string? AuthJson { get; set; }
        public string? OptionsJson { get; set; }
        public long CreatedAtTs { get; set; }
        public long UpdatedAtTs { get; set; }
    }
}
