using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Models.Settings;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Feedarr.Api.Services.Security;

/// <summary>
/// Service de migration des clés API existantes vers le format chiffré.
/// Exécuté au démarrage de l'application.
/// </summary>
public sealed class ApiKeyMigrationService
{
    private static readonly string[] ExternalApiSettingKeys =
    {
        "tmdb_api_key",
        "tvmaze_api_key",
        "fanart_api_key",
        "igdb_client_id",
        "igdb_client_secret"
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly Db _db;
    private readonly IApiKeyProtectionService _protectionService;
    private readonly ILogger<ApiKeyMigrationService> _logger;

    public ApiKeyMigrationService(
        Db db,
        IApiKeyProtectionService protectionService,
        ILogger<ApiKeyMigrationService> logger)
    {
        _db = db;
        _protectionService = protectionService;
        _logger = logger;
    }

    /// <summary>
    /// Migre toutes les clés API non chiffrées vers le format chiffré.
    /// </summary>
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Démarrage de la migration des clés API...");

        using var conn = _db.Open();

        var pendingCount = await CountPendingMigrationsAsync(conn, cancellationToken);
        if (pendingCount == 0)
        {
            _logger.LogInformation("Aucune clé API à migrer");
            return;
        }

        var backupPath = CreateBackupPath();
        CreateDatabaseBackup(conn, backupPath);
        _logger.LogInformation("Backup pré-migration créé: {BackupPath}", backupPath);

        using var tx = conn.BeginTransaction();
        try
        {
            var sourcesCount = await MigrateSourcesAsync(conn, tx, cancellationToken);
            var providersCount = await MigrateProvidersAsync(conn, tx, cancellationToken);
            var arrAppsCount = await MigrateArrApplicationsAsync(conn, tx, cancellationToken);
            var externalSettingsCount = await MigrateExternalSettingsAsync(conn, tx, cancellationToken);
            var legacyExternalCount = await MigrateLegacyExternalJsonAsync(conn, tx, cancellationToken);
            var externalInstancesCount = await MigrateExternalProviderInstancesAsync(conn, tx, cancellationToken);

            tx.Commit();

            _logger.LogInformation(
                "Migration terminée: {Sources} source(s), {Providers} provider(s), {ArrApps} app(s) Arr migrés, {External} setting(s) externes migrés, {LegacyExternal} valeur(s) legacy migrées, {ExternalInstances} instance(s) metadata migrées",
                sourcesCount, providersCount, arrAppsCount, externalSettingsCount, legacyExternalCount, externalInstancesCount);
        }
        catch (Exception ex)
        {
            try
            {
                tx.Rollback();
            }
            catch (Exception rollbackEx)
            {
                _logger.LogError(rollbackEx, "Échec du rollback de la migration des clés API");
            }

            _logger.LogError(ex, "Migration des clés API annulée et rollback effectué. Backup disponible: {BackupPath}", backupPath);
            throw;
        }
    }

    private async Task<int> MigrateSourcesAsync(SqliteConnection conn, SqliteTransaction tx, CancellationToken cancellationToken)
    {
        var sources = await conn.QueryAsync<(long Id, string? ApiKey)>(
            "SELECT id, api_key FROM sources WHERE api_key IS NOT NULL AND api_key <> ''",
            transaction: tx);

        var migratedCount = 0;
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(source.ApiKey))
                continue;

            // Si déjà chiffrée, ignorer
            if (_protectionService.IsProtected(source.ApiKey))
                continue;

            try
            {
                var encrypted = _protectionService.Protect(source.ApiKey);
                await conn.ExecuteAsync(
                    "UPDATE sources SET api_key = @key WHERE id = @id",
                    new { key = encrypted, id = source.Id },
                    tx);

                migratedCount++;
                _logger.LogDebug("Source {Id}: clé API chiffrée", source.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors du chiffrement de la clé API pour source {Id}", source.Id);
                throw new InvalidOperationException($"API key migration failed for source {source.Id}", ex);
            }
        }

        return migratedCount;
    }

    private async Task<int> MigrateProvidersAsync(SqliteConnection conn, SqliteTransaction tx, CancellationToken cancellationToken)
    {
        var providers = await conn.QueryAsync<(long Id, string? ApiKey)>(
            "SELECT id, api_key FROM providers WHERE api_key IS NOT NULL AND api_key <> ''",
            transaction: tx);

        var migratedCount = 0;
        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(provider.ApiKey))
                continue;

            // Si déjà chiffrée, ignorer
            if (_protectionService.IsProtected(provider.ApiKey))
                continue;

            try
            {
                var encrypted = _protectionService.Protect(provider.ApiKey);
                await conn.ExecuteAsync(
                    "UPDATE providers SET api_key = @key WHERE id = @id",
                    new { key = encrypted, id = provider.Id },
                    tx);

                migratedCount++;
                _logger.LogDebug("Provider {Id}: clé API chiffrée", provider.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors du chiffrement de la clé API pour provider {Id}", provider.Id);
                throw new InvalidOperationException($"API key migration failed for provider {provider.Id}", ex);
            }
        }

        return migratedCount;
    }

    private async Task<int> MigrateArrApplicationsAsync(SqliteConnection conn, SqliteTransaction tx, CancellationToken cancellationToken)
    {
        var apps = await conn.QueryAsync<(long Id, string? ApiKey)>(
            "SELECT id, api_key_encrypted FROM arr_applications WHERE api_key_encrypted IS NOT NULL AND api_key_encrypted <> ''",
            transaction: tx);

        var migratedCount = 0;
        foreach (var app in apps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(app.ApiKey))
                continue;

            if (_protectionService.IsProtected(app.ApiKey))
                continue;

            try
            {
                var encrypted = _protectionService.Protect(app.ApiKey);
                await conn.ExecuteAsync(
                    "UPDATE arr_applications SET api_key_encrypted = @key WHERE id = @id",
                    new { key = encrypted, id = app.Id },
                    tx);

                migratedCount++;
                _logger.LogDebug("Arr application {Id}: clé API chiffrée", app.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors du chiffrement de la clé API pour app Arr {Id}", app.Id);
                throw new InvalidOperationException($"API key migration failed for Arr app {app.Id}", ex);
            }
        }

        return migratedCount;
    }

    private async Task<int> MigrateExternalSettingsAsync(SqliteConnection conn, SqliteTransaction tx, CancellationToken cancellationToken)
    {
        var rows = await conn.QueryAsync<(string Key, string? ValueJson)>(
            "SELECT key as Key, value_json as ValueJson FROM app_settings WHERE key IN @keys",
            new { keys = ExternalApiSettingKeys },
            tx);

        var migratedCount = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = row.ValueJson;
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var currentValue = DeserializeStringSetting(raw);
            if (string.IsNullOrWhiteSpace(currentValue))
                continue;

            if (_protectionService.IsProtected(currentValue))
                continue;

            try
            {
                var encryptedValue = _protectionService.Protect(currentValue);
                var valueJson = JsonSerializer.Serialize(encryptedValue, JsonOpts);
                await conn.ExecuteAsync(
                    "UPDATE app_settings SET value_json = @valueJson WHERE key = @key",
                    new { key = row.Key, valueJson },
                    tx);
                migratedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors du chiffrement de la clé externe {SettingKey}", row.Key);
                throw new InvalidOperationException($"API key migration failed for setting {row.Key}", ex);
            }
        }

        return migratedCount;
    }

    private async Task<int> MigrateLegacyExternalJsonAsync(SqliteConnection conn, SqliteTransaction tx, CancellationToken cancellationToken)
    {
        var valueJson = await conn.ExecuteScalarAsync<string?>(
            "SELECT value_json FROM app_settings WHERE key = 'external'",
            transaction: tx);

        if (string.IsNullOrWhiteSpace(valueJson))
            return 0;

        ExternalSettings? external;
        try
        {
            external = JsonSerializer.Deserialize<ExternalSettings>(valueJson, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impossible de parser la configuration legacy external");
            return 0;
        }

        if (external is null)
            return 0;

        var migratedCount = 0;
        (external.TmdbApiKey, migratedCount) = ProtectLegacyKey(external.TmdbApiKey, migratedCount);
        (external.TvmazeApiKey, migratedCount) = ProtectLegacyKey(external.TvmazeApiKey, migratedCount);
        (external.FanartApiKey, migratedCount) = ProtectLegacyKey(external.FanartApiKey, migratedCount);
        (external.IgdbClientId, migratedCount) = ProtectLegacyKey(external.IgdbClientId, migratedCount);
        (external.IgdbClientSecret, migratedCount) = ProtectLegacyKey(external.IgdbClientSecret, migratedCount);

        if (migratedCount == 0)
            return 0;

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var updatedJson = JsonSerializer.Serialize(external, JsonOpts);
            await conn.ExecuteAsync(
                "UPDATE app_settings SET value_json = @valueJson WHERE key = 'external'",
                new { valueJson = updatedJson },
                tx);
            return migratedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la mise à jour de la configuration legacy external");
            throw new InvalidOperationException("API key migration failed for legacy external settings", ex);
        }
    }

    private async Task<int> MigrateExternalProviderInstancesAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(conn, "external_provider_instances", tx))
            return 0;

        var rows = await conn.QueryAsync<(string InstanceId, string? AuthJson)>(
            """
            SELECT instance_id AS InstanceId, auth_json AS AuthJson
            FROM external_provider_instances
            WHERE auth_json IS NOT NULL AND auth_json <> '' AND auth_json <> '{}';
            """,
            transaction: tx);

        var migratedCount = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var auth = DeserializeAuthMap(row.AuthJson);
            if (auth.Count == 0)
                continue;

            var changed = false;
            foreach (var key in auth.Keys.ToList())
            {
                var value = auth[key];
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                var trimmed = value.Trim();
                if (_protectionService.IsProtected(trimmed))
                    continue;

                auth[key] = _protectionService.Protect(trimmed);
                changed = true;
            }

            if (!changed)
                continue;

            try
            {
                var updatedJson = JsonSerializer.Serialize(auth, JsonOpts);
                await conn.ExecuteAsync(
                    "UPDATE external_provider_instances SET auth_json = @authJson WHERE instance_id = @instanceId",
                    new { authJson = updatedJson, instanceId = row.InstanceId },
                    tx);
                migratedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors du chiffrement de auth_json pour instance metadata {InstanceId}", row.InstanceId);
                throw new InvalidOperationException($"API key migration failed for metadata instance {row.InstanceId}", ex);
            }
        }

        return migratedCount;
    }

    private static string? DeserializeStringSetting(string valueJson)
    {
        try
        {
            return JsonSerializer.Deserialize<string>(valueJson, JsonOpts);
        }
        catch
        {
            return valueJson.Trim().Trim('"');
        }
    }

    private (string? value, int migratedCount) ProtectLegacyKey(string? value, int migratedCount)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (value, migratedCount);

        var trimmed = value.Trim();
        if (_protectionService.IsProtected(trimmed))
            return (trimmed, migratedCount);

        var encrypted = _protectionService.Protect(trimmed);
        return (encrypted, migratedCount + 1);
    }

    private async Task<int> CountPendingMigrationsAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pending = 0;

        pending += await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM sources WHERE api_key IS NOT NULL AND api_key <> '' AND api_key NOT LIKE 'ENC:%'");
        pending += await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM providers WHERE api_key IS NOT NULL AND api_key <> '' AND api_key NOT LIKE 'ENC:%'");
        pending += await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM arr_applications WHERE api_key_encrypted IS NOT NULL AND api_key_encrypted <> '' AND api_key_encrypted NOT LIKE 'ENC:%'");

        var settingRows = await conn.QueryAsync<string?>(
            "SELECT value_json FROM app_settings WHERE key IN @keys",
            new { keys = ExternalApiSettingKeys });
        foreach (var raw in settingRows)
        {
            var currentValue = DeserializeStringSetting(raw ?? string.Empty);
            if (string.IsNullOrWhiteSpace(currentValue))
                continue;
            if (!_protectionService.IsProtected(currentValue.Trim()))
                pending++;
        }

        var legacyValueJson = await conn.ExecuteScalarAsync<string?>(
            "SELECT value_json FROM app_settings WHERE key = 'external'");
        pending += CountUnprotectedLegacyExternalKeys(legacyValueJson);
        pending += await CountPendingExternalProviderInstancesAsync(conn, cancellationToken);

        return pending;
    }

    private async Task<int> CountPendingExternalProviderInstancesAsync(
        SqliteConnection conn,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!await TableExistsAsync(conn, "external_provider_instances"))
            return 0;

        var rows = await conn.QueryAsync<string?>(
            """
            SELECT auth_json
            FROM external_provider_instances
            WHERE auth_json IS NOT NULL AND auth_json <> '' AND auth_json <> '{}';
            """);

        var pending = 0;
        foreach (var row in rows)
        {
            var auth = DeserializeAuthMap(row);
            foreach (var value in auth.Values)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                if (!_protectionService.IsProtected(value.Trim()))
                    pending++;
            }
        }

        return pending;
    }

    private static Dictionary<string, string?> DeserializeAuthMap(string? authJson)
    {
        if (string.IsNullOrWhiteSpace(authJson))
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var doc = JsonDocument.Parse(authJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                map[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => prop.Value.GetString(),
                    _ => prop.Value.GetRawText()
                };
            }

            return map;
        }
        catch
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection conn,
        string tableName,
        SqliteTransaction? tx = null)
    {
        var exists = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = @tableName",
            new { tableName },
            tx);
        return exists > 0;
    }

    private int CountUnprotectedLegacyExternalKeys(string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
            return 0;

        ExternalSettings? external;
        try
        {
            external = JsonSerializer.Deserialize<ExternalSettings>(valueJson, JsonOpts);
        }
        catch
        {
            return 0;
        }

        if (external is null)
            return 0;

        var count = 0;
        if (NeedsProtection(external.TmdbApiKey)) count++;
        if (NeedsProtection(external.TvmazeApiKey)) count++;
        if (NeedsProtection(external.FanartApiKey)) count++;
        if (NeedsProtection(external.IgdbClientId)) count++;
        if (NeedsProtection(external.IgdbClientSecret)) count++;
        return count;
    }

    private bool NeedsProtection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return !_protectionService.IsProtected(value.Trim());
    }

    private string CreateBackupPath()
    {
        var dbPath = Path.GetFullPath(_db.DbPath);
        var dbDir = Path.GetDirectoryName(dbPath) ?? ".";
        var backupDir = Path.Combine(dbDir, "backups");
        Directory.CreateDirectory(backupDir);

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return Path.Combine(backupDir, $"feedarr-pre-api-key-migration-{stamp}-{suffix}.db");
    }

    private static void CreateDatabaseBackup(SqliteConnection sourceConn, string backupPath)
    {
        using var backupConn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString());

        backupConn.Open();
        sourceConn.BackupDatabase(backupConn);
    }
}
