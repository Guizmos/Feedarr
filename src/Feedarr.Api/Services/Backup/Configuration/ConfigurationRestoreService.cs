using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Data.Sqlite;

namespace Feedarr.Api.Services.Backup.Configuration;

// A preview is read-only. Apply uses one transaction; nothing is staged on disk.
public sealed class ConfigurationRestoreService(Db db, ConfigurationArchiveService archives,
    IApiKeyProtectionService protection, ExternalProviderRegistry registry,
    BackupExecutionCoordinator coordinator, IMemoryCache cache, OutboundDestinationPolicy outboundPolicy)
{
    internal Action<string>? FaultPoint { get; set; }
    private bool _restartRequired;
    public async Task<ConfigurationRestorePreview> PreviewAsync(Stream input, string password, CancellationToken ct = default)
    {
        var read = await archives.ReadAsync(input, password, ct);
        ValidateDestinations(read.Configuration);
        using var c = db.Open(); using var tx = c.BeginTransaction(deferred: true);
        var plan = ConfigurationRestorePlanner.Build(c, tx, read.Configuration);
        return new(ConfigurationRestorePlanner.Fingerprint(c, tx), read.Preview,
            plan.Sources.Values.Count(x => x.HasValue), plan.Sources.Values.Count(x => !x.HasValue),
            c.Query<long>("SELECT id FROM sources WHERE enabled=1", transaction: tx).Count(id => !plan.Sources.Values.Contains(id)));
    }
    public async Task<ConfigurationRestoreResult> ApplyAsync(Stream input, string password, string expectedState, CancellationToken ct = default)
    {
        var read = await archives.ReadAsync(input, password, ct);
        ValidateDestinations(read.Configuration);
        return await coordinator.RunExclusiveAsync("restore-configuration", null, token =>
        {
            using var c = db.Open(); c.Execute("PRAGMA synchronous=FULL");
            using var tx = c.BeginTransaction(deferred: false);
            if (ConfigurationRestorePlanner.Fingerprint(c, tx) != expectedState)
                throw new BackupOperationException("La configuration a changé. Analysez de nouveau la sauvegarde.", 409);
            var plan = ConfigurationRestorePlanner.Build(c, tx, read.Configuration);
            var previous = ConfigurationRestorePlanner.Setting(c, tx, "maintenance", new MaintenanceSettings());
            var next = read.Configuration.Settings["maintenance"].Deserialize<MaintenanceSettings>(ConfigurationSchema.Json)!;
            var restart = previous.PosterWorkers != next.PosterWorkers || previous.ProviderRateLimitMode != next.ProviderRateLimitMode ||
                next.ProviderRateLimitMode == "manual" && !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(previous.ProviderConcurrencyManual), JsonSerializer.SerializeToNode(next.ProviderConcurrencyManual));
            restart |= IgdbCredentialsChanged(c, tx, read.Configuration);
            ProtectSecrets(read.Configuration);
            token.ThrowIfCancellationRequested(); Apply(c, tx, read.Configuration, plan);
            FaultPoint?.Invoke("before-commit"); token.ThrowIfCancellationRequested();
            var providerIds = c.Query<long>("SELECT id FROM providers", transaction: tx).ToArray();
            tx.Commit();
            _restartRequired |= restart;
            // Category/discovery caches already bind their keys to connection fingerprints.
            foreach (var id in providerIds) cache.Remove($"provider:indexers:{id}");
            cache.Remove("system:status:v1");
            return Task.FromResult(new ConfigurationRestoreResult(true, _restartRequired));
        }, ct);
    }
    private bool IgdbCredentialsChanged(SqliteConnection c, SqliteTransaction tx, ConfigurationDocument model)
    {
        var current = c.ExecuteScalar<string?>("SELECT auth_json FROM external_provider_instances WHERE lower(provider_key)='igdb' AND enabled=1 ORDER BY updated_at_ts DESC,created_at_ts DESC LIMIT 1", transaction: tx);
        var incoming = model.ExternalProviders.Where(x => Text(x, "provider_key").Equals("igdb", StringComparison.OrdinalIgnoreCase) && x["enabled"]!.GetValue<long>() == 1).OrderBy(x => x["priority"]!.GetValue<long>()).FirstOrDefault();
        if (current is null || incoming is null) return current is not null || incoming is not null;
        try
        {
            var auth = ConfigurationArchiveService.ParseObject(current);
            foreach (var field in new[] { "clientId", "clientSecret" })
                if (!protection.TryUnprotect(Text(auth, field), out var old) || old != Text(incoming["auth_json"]!.AsObject(), field)) return true;
            return false;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return true; }
    }
    private void Apply(SqliteConnection c, SqliteTransaction tx, ConfigurationDocument model, ConfigurationRestorePlan plan)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var wasComplete = ConfigurationRestorePlanner.Setting(c, tx, "ui", new UiSettings()).OnboardingDone;
        // Keep every historical row, even when it currently has no releases.
        // No deletes on tables owning cascade relationships are ever issued.
        c.Execute("UPDATE providers SET enabled=0; UPDATE sources SET enabled=0; UPDATE arr_applications SET is_enabled=0,is_default=0; UPDATE external_provider_instances SET enabled=0;", transaction: tx);
        var providerIds = new Dictionary<string, long>(); var sourceIds = new Dictionary<string, long>(); var appIds = new Dictionary<string, long>();
        foreach (var row in model.Providers)
        {
            var values = Clone(row); values.Remove("key"); values["api_key_encrypted"] = values["api_key"]?.DeepClone(); values.Remove("api_key");
            providerIds[Text(row, "key")] = Upsert("providers", plan.Providers[Text(row, "key")], values);
        }
        FaultPoint?.Invoke("after-providers");
        foreach (var row in model.Sources)
        {
            var values = Clone(row); values.Remove("key"); values.Remove("providerKey");
            values["provider_id"] = Text(row, "providerKey").Length == 0 ? null : JsonValue.Create(providerIds[Text(row, "providerKey")]);
            var id = Upsert("sources", plan.Sources[Text(row, "key")], values); sourceIds[Text(row, "key")] = id;
            c.Execute("DELETE FROM source_selected_categories WHERE source_id=@id; DELETE FROM source_category_mappings WHERE source_id=@id;", new { id }, tx);
        }
        foreach (var row in model.Applications)
        { var values = Clone(row); values.Remove("key"); appIds[Text(row, "key")] = Upsert("arr_applications", plan.Applications[Text(row, "key")], values); }
        var ordinal = 0;
        foreach (var row in model.ExternalProviders.OrderBy(x => x["priority"]!.GetValue<long>()))
        {
            var values = Clone(row); values.Remove("key"); values.Remove("priority");
            var id = plan.ExternalProviders[Text(row, "key")] ?? Guid.NewGuid().ToString("N");
            values["updated_at_ts"] = now + model.ExternalProviders.Count - ordinal++;
            var parameters = Parameters(values); parameters.Add("id", id);
            if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM external_provider_instances WHERE instance_id=@id", new { id }, tx) > 0)
                c.Execute($"UPDATE external_provider_instances SET {Assignments(values)} WHERE instance_id=@id", parameters, tx);
            else
            {
                values["instance_id"] = id; values["created_at_ts"] = now;
                Insert("external_provider_instances", values);
            }
        }
        foreach (var row in model.Categories)
        {
            var values = Clone(row); values.Remove("sourceKey"); values["source_id"] = sourceIds[Text(row, "sourceKey")];
            var parameters = Parameters(values);
            c.Execute($"INSERT INTO source_categories ({Columns(values)}) VALUES ({Placeholders(values)}) ON CONFLICT(source_id,cat_id) DO UPDATE SET {Assignments(values)}", parameters, tx);
        }
        foreach (var (table, rows) in new[] { ("source_category_mappings", model.Mappings), ("source_selected_categories", model.Selections) })
            foreach (var row in rows)
            {
                var values = Clone(row); values.Remove("sourceKey"); values["source_id"] = sourceIds[Text(row, "sourceKey")];
                if (table == "source_category_mappings") { values["created_at_ts"] = now; values["updated_at_ts"] = now; }
                Insert(table, values);
            }
        foreach (var setting in model.Settings) SaveSetting(setting.Key, setting.Value.DeepClone());
        var ui = Clone(model.Settings["ui"]);
        var sourceFilter = Text(ui, "defaultFilterSourceId"); var appFilter = Text(ui, "defaultFilterApplication");
        ui["defaultFilterSourceId"] = sourceIds.TryGetValue(sourceFilter, out var sourceId) ? sourceId.ToString() : "";
        ui["defaultFilterApplication"] = appIds.TryGetValue(appFilter, out var appId) ? appId.ToString() : appFilter == "__hide_apps__" ? appFilter : "";
        ui["onboardingDone"] = wasComplete;
        SaveSetting("ui", ui);
        // Canonical instances also project to the legacy compatibility settings.
        c.Execute("DELETE FROM app_settings WHERE key='external'", transaction: tx);
        foreach (var (provider, fields) in new[]{("tmdb",new[]{("apiKey","tmdb_api_key")}),
            ("tvmaze",new[]{("apiKey","tvmaze_api_key")}), ("fanart",new[]{("apiKey","fanart_api_key")}),
            ("igdb",new[]{("clientId","igdb_client_id"),("clientSecret","igdb_client_secret")})})
        {
            var active = model.ExternalProviders.Where(x => Text(x, "provider_key") == provider && x["enabled"]!.GetValue<long>() == 1).OrderBy(x => x["priority"]!.GetValue<long>()).FirstOrDefault();
            foreach (var (field, key) in fields) SaveSetting(key, JsonValue.Create(active?["auth_json"]?[field]?.GetValue<string>() ?? ""));
            SaveSetting(provider + "_enabled", JsonValue.Create(active is null ? "0" : "1"));
        }
        if (c.Query("PRAGMA foreign_key_check", transaction: tx).Any()) throw Changed();
        FaultPoint?.Invoke("after-configuration");

        long Upsert(string table, long? target, JsonObject values)
        {
            values["updated_at_ts"] = now;
            if (target.HasValue)
            {
                var parameters = Parameters(values); parameters.Add("id", target.Value);
                if (c.Execute($"UPDATE {table} SET {Assignments(values)} WHERE id=@id", parameters, tx) != 1) throw Changed();
                return target.Value;
            }
            values["created_at_ts"] = now; Insert(table, values);
            return c.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
        }
        void Insert(string table, JsonObject values) => c.Execute($"INSERT INTO {table} ({Columns(values)}) VALUES ({Placeholders(values)})", Parameters(values), tx);
        void SaveSetting(string key, JsonNode? value) => c.Execute("""
            INSERT INTO app_settings(key,value_json,updated_at_ts) VALUES(@key,@json,@now)
            ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json,updated_at_ts=excluded.updated_at_ts
            """, new { key, json = value?.ToJsonString(ConfigurationSchema.Json) ?? "null", now }, tx);
    }

    private void ProtectSecrets(ConfigurationDocument model)
    {
        foreach (var row in model.Providers.Concat(model.Sources)) Protect(row, "api_key");
        foreach (var row in model.Applications) Protect(row, "api_key_encrypted");
        foreach (var row in model.ExternalProviders)
        {
            registry.TryGet(Text(row, "provider_key"), out var definition);
            foreach (var field in definition.FieldsSchema) Protect((JsonObject)row["auth_json"]!, field.Key);
        }
        void Protect(JsonObject row, string field)
        {
            var value = Text(row, field); if (value.Length == 0) return;
            if (protection.IsProtected(value)) throw new BackupOperationException("configuration_restore_invalid_credential", 422);
            var encrypted = protection.Protect(value);
            if (!protection.TryUnprotect(encrypted, out var verified) || verified != value) throw new BackupOperationException("configuration_restore_credential_protection_failed", 422);
            row[field] = encrypted;
        }
    }
    private void ValidateDestinations(ConfigurationDocument model)
    {
        foreach (var row in model.Providers.Concat(model.Applications).Concat(model.ExternalProviders))
            if (Text(row, "base_url").Length > 0 && !OutboundUrlGuard.TryNormalizeHttpBaseUrl(Text(row, "base_url"), false, out _, out _, outboundPolicy,
                model.ExternalProviders.Contains(row) ? OutboundDestinationContext.Strict : OutboundDestinationContext.TrustedIntegration)) throw new BackupOperationException("configuration_restore_destination_not_allowed", 422);
        foreach (var row in model.Sources)
            if (!OutboundUrlGuard.TryNormalizeTorznabUrl(Text(row, "torznab_url"), out _, out _, outboundPolicy, OutboundDestinationContext.TrustedIntegration)) throw new BackupOperationException("configuration_restore_destination_not_allowed", 422);
    }
    private static BackupOperationException Changed() => new("La configuration a changé pendant la restauration.", 409);
    private static JsonObject Clone(JsonObject value) => (JsonObject)value.DeepClone();
    private static string Text(JsonObject row, string key) => ConfigurationSchema.Text(row, key);
    private static string Columns(JsonObject row) => string.Join(',', row.Select(x => x.Key));
    private static string Placeholders(JsonObject row) => string.Join(',', row.Select(x => "@" + x.Key));
    private static string Assignments(JsonObject row) => string.Join(',', row.Select(x => x.Key + "=@" + x.Key));
    private static DynamicParameters Parameters(JsonObject row)
    {
        var p = new DynamicParameters();
        foreach (var (key, value) in row) p.Add(key, value is null ? null : value is JsonObject ? value.ToJsonString() : value.GetValueKind() == JsonValueKind.Number ? value.GetValue<long>() : value.GetValue<string>());
        return p;
    }
}
public sealed record ConfigurationRestorePreview(string ExpectedState, ConfigurationPreview Configuration, int MatchedSources, int NewSources, int DisabledSources);
public sealed record ConfigurationRestoreResult(bool Applied, bool RestartRequired);
