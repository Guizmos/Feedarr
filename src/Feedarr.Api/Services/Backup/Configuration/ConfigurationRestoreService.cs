using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Data.Sqlite;

namespace Feedarr.Api.Services.Backup.Configuration;

/// <summary>Control-plane only: preparation never changes active configuration;
/// application is called at startup, before workers or HTTP start.</summary>
public sealed class ConfigurationRestoreService(
    Db db, ConfigurationArchiveService archives, ConfigurationRestorePlanStore store,
    IApiKeyProtectionService protection, ExternalProviderRegistry registry,
    BackupExecutionCoordinator coordinator, PendingRestoreStore legacy,
    SetupStateService setup, BootstrapTokenService bootstrapTokens, IMemoryCache cache,
    IConfiguration configuration, OutboundDestinationPolicy outboundPolicy)
{
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    internal Action<string>? FaultPoint { get; set; }

    public Task<ConfigurationRestorePreview> PrepareAsync(Stream archive, string passphrase, HttpContext caller, CancellationToken ct = default)
    {
        Authorize(caller);
        return coordinator.RunExclusiveAsync("prepare-configuration", null, async token =>
        {
            if (store.Read() is not null) throw ConfigurationRestorePlanStore.Conflict();
            legacy.EnsureCanPrepare(null);
            using (var conn = db.Open())
                if (conn.ExecuteScalar<int>("SELECT COUNT(*) FROM configuration_restore_operations WHERE initial_sync_state<>'done'") > 0)
                    throw ConfigurationRestorePlanStore.Conflict();
            var read = await archives.ReadAsync(archive, passphrase, token).ConfigureAwait(false);
            ValidateDestinations(read.Configuration);
            using var c = db.Open(); using var tx = c.BeginTransaction(deferred: true);
            var plan = ConfigurationRestorePlanner.Build(c, tx, read.Configuration, read.Preview, HasMediaFiles());
            tx.Commit();
            ProtectSecrets(plan.Configuration);
            token.ThrowIfCancellationRequested();
            store.Write(plan);
            return plan.Preview;
        }, ct);
    }

    public ConfigurationRestorePreview? GetPendingPreview(HttpContext caller)
    {
        Authorize(caller);
        return store.Read()?.Preview;
    }

    public Task ConfirmAsync(string operationId, HttpContext caller, bool confirmSecurityChange = false, CancellationToken ct = default)
    {
        Authorize(caller);
        return coordinator.RunExclusiveAsync("confirm-configuration", null, token =>
        {
            var plan = GetPlan(operationId);
            if (plan.Preview.SecurityChangeRequiresConfirmation && !confirmSecurityChange)
                throw new BackupOperationException("configuration_restore_security_confirmation_required", 409);
            ConfigurationRestorePlanStore.EnsureNoLegacyConflict(Path.GetDirectoryName(Path.GetFullPath(db.DbPath))!);
            using var c = db.Open(); using var tx = c.BeginTransaction(deferred: true);
            if (ConfigurationRestorePlanner.Fingerprint(c, tx) != plan.Baseline) throw Changed();
            token.ThrowIfCancellationRequested(); plan.Confirmed = true; store.Write(plan);
            return Task.FromResult(true);
        }, ct);
    }

    public Task CancelAsync(string operationId, HttpContext caller, CancellationToken ct = default)
    {
        Authorize(caller);
        return coordinator.RunExclusiveAsync("cancel-configuration", null, _ =>
        { GetPlan(operationId); store.Clear(); return Task.FromResult(true); }, ct);
    }

    internal void ApplyAtStartup()
    {
        var plan = store.Read(); if (plan is null || !plan.Confirmed) return;
        ConfigurationRestorePlanStore.EnsureNoLegacyConflict(Path.GetDirectoryName(Path.GetFullPath(db.DbPath))!);
        using var c = db.Open();
        // FULL durability for the configuration transaction and its replay marker.
        c.Execute("PRAGMA synchronous=FULL;");
        using (var tx = c.BeginTransaction(deferred: false))
        {
            if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM configuration_restore_operations WHERE id=@id", new { id = plan.Id }, tx) > 0)
            { tx.Commit(); Invalidate(); store.Clear(); return; }
            if (ConfigurationRestorePlanner.Fingerprint(c, tx) != plan.Baseline ||
                ConfigurationRestorePlanner.HasLibrary(c, tx, HasMediaFiles()) != plan.Preview.HasLibrary) throw Changed();
            ValidateDestinations(plan.Configuration);
            VerifyDestinationSecrets(plan.Configuration);
            Apply(c, tx, plan);
            FaultPoint?.Invoke("before-commit");
            tx.Commit();
        }
        FaultPoint?.Invoke("after-commit");
        Invalidate(); store.Clear();
    }

    private void Apply(SqliteConnection c, SqliteTransaction tx, ConfigurationRestorePlan plan)
    {
        var model = plan.Configuration; var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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
        ui["onboardingDone"] = plan.Preview.CanCompleteSetup || !plan.Preview.FreshInstallation && wasComplete;
        SaveSetting("ui", ui);
        int? pendingPer = null, pendingGlobal = null;
        if (plan.Preview.StricterRetention)
        {
            var general = Clone(model.Settings["general"]);
            pendingPer = general["rssLimitPerCategory"]?.GetValue<int>() ?? 50;
            pendingGlobal = general["rssLimitGlobalPerSource"]?.GetValue<int>() ?? 250;
            general["rssLimitPerCategory"] = plan.PreviousPerCategory;
            general["rssLimitGlobalPerSource"] = plan.PreviousGlobal;
            SaveSetting("general", general);
        }
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
        var ids = sourceIds.Values.Where(id => c.ExecuteScalar<int>("SELECT enabled FROM sources WHERE id=@id", new { id }, tx) == 1).ToArray();
        var state = ids.Length == 0 ? "done" : plan.Preview.HasLibrary || !plan.Preview.CanCompleteSetup ? "held" : "pending";
        c.Execute("UPDATE configuration_restore_operations SET retention_per_category=NULL,retention_global=NULL WHERE initial_sync_state='done'", transaction: tx);
        c.Execute("""
            INSERT INTO configuration_restore_operations(id,applied_at_ts,initial_sync_state,source_ids_json,retention_per_category,retention_global)
            VALUES(@id,@now,@state,@ids,@pendingPer,@pendingGlobal)
            """, new { id = plan.Id, now, state, ids = JsonSerializer.Serialize(ids), pendingPer, pendingGlobal }, tx);
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

    public Task ConfirmRetentionAsync(string operationId, HttpContext caller, CancellationToken ct = default)
    {
        Authorize(caller);
        return coordinator.RunExclusiveAsync("confirm-configuration-retention", null, _ =>
        {
            using var c = db.Open(); using var tx = c.BeginTransaction(deferred: false);
            var limits = c.QuerySingleOrDefault<(int? Per, int? Global)>("SELECT retention_per_category,retention_global FROM configuration_restore_operations WHERE id=@operationId", new { operationId }, tx);
            if (limits.Per is null || limits.Global is null) throw Changed();
            var general = ConfigurationRestorePlanner.Setting(c, tx, "general", new GeneralSettings());
            general.RssLimitPerCategory = limits.Per.Value; general.RssLimitGlobalPerSource = limits.Global.Value;
            c.Execute("UPDATE app_settings SET value_json=@json WHERE key='general'; UPDATE configuration_restore_operations SET retention_per_category=NULL,retention_global=NULL WHERE id=@operationId",
                new { json = JsonSerializer.Serialize(general, ConfigurationSchema.Json), operationId }, tx);
            tx.Commit(); return Task.FromResult(true);
        }, ct);
    }

    public Task ConfirmInitialSyncAsync(string operationId, HttpContext caller, CancellationToken ct = default)
    {
        Authorize(caller);
        return coordinator.RunExclusiveAsync("confirm-configuration-sync", null, _ =>
        {
            using var c = db.Open(); var current = new ConfigurationSnapshotReader(protection, registry).Read(c, false, out var unreadable);
            if (!ConfigurationSchema.Validate(current, registry).IsComplete || !setup.IsSetupCompleted()) throw Changed();
            if (c.Execute("UPDATE configuration_restore_operations SET initial_sync_state='pending',next_attempt_ts=0 WHERE id=@operationId AND initial_sync_state='held' AND retention_per_category IS NULL", new { operationId }) != 1) throw Changed();
            return Task.FromResult(true);
        }, ct);
    }

    public Task CancelInitialSyncAsync(string operationId, HttpContext caller, CancellationToken ct = default)
    {
        Authorize(caller);
        return coordinator.RunExclusiveAsync("cancel-configuration-sync", null, _ =>
        {
            using var c = db.Open();
            if (c.Execute("UPDATE configuration_restore_operations SET initial_sync_state='done' WHERE id=@operationId AND initial_sync_state<>'done'", new { operationId }) != 1) throw Changed();
            return Task.FromResult(true);
        }, ct);
    }

    internal async Task<bool> RunInitialSyncCycleAsync(Func<long, CancellationToken, Task<bool>> run, CancellationToken ct)
    {
        if (!await _syncGate.WaitAsync(0, ct).ConfigureAwait(false)) return true;
        IDisposable? activity = null;
        try
        {
            using var c = db.Open();
            if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM configuration_restore_operations WHERE initial_sync_state<>'done'") == 0) return false;
            InitialSyncJob job;
            using (var claim = c.BeginTransaction(deferred: false))
            {
                job = c.QuerySingleOrDefault<InitialSyncJob>("SELECT id,initial_sync_state AS State,source_ids_json AS Sources,completed_ids_json AS Completed,next_attempt_ts AS Next,attempts FROM configuration_restore_operations WHERE initial_sync_state<>'done' ORDER BY applied_at_ts LIMIT 1", transaction: claim)!;
                if (job is null) return false;
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (job.State == "held" || job.Next > now || store.BlocksSync) return true;
                activity = coordinator.TryEnterSyncActivity("configuration-initial-sync");
                if (activity is null) return true;
                var next = now + Math.Min(3600, 60 * (1L << Math.Min(job.Attempts, 6)));
                c.Execute("UPDATE configuration_restore_operations SET initial_sync_state='running',attempts=attempts+1,next_attempt_ts=@next WHERE id=@Id", new { next, job.Id }, claim);
                claim.Commit();
            }
            var ids = JsonSerializer.Deserialize<long[]>(job.Sources)!;
            var done = JsonSerializer.Deserialize<HashSet<long>>(job.Completed)!;
            try
            {
                foreach (var id in ids.Where(id => !done.Contains(id)))
                {
                    ct.ThrowIfCancellationRequested();
                    bool ok;
                    try { ok = await run(id, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { ok = false; } // never persist provider URLs or errors
                    if (ok)
                    {
                        done.Add(id); c.Execute("UPDATE configuration_restore_operations SET completed_ids_json=@json WHERE id=@Id", new { json = JsonSerializer.Serialize(done), job.Id });
                    }
                }
            }
            finally
            {
                c.Execute("UPDATE configuration_restore_operations SET initial_sync_state=@state WHERE id=@Id", new { state = ids.All(done.Contains) ? "done" : "pending", job.Id });
            }
            return true; // skip the ordinary scheduler pass even on completion
        }
        finally { activity?.Dispose(); _syncGate.Release(); }
    }

    internal void AuthorizeRequest(HttpContext caller) => Authorize(caller);

    public ConfigurationRestoreState GetState(HttpContext caller)
    {
        Authorize(caller);
        var pending=store.Read();
        using var c=db.Open();
        var job=c.QuerySingleOrDefault<ConfigurationAppliedState>("""
            SELECT id AS OperationId,initial_sync_state AS SyncState,
                json_array_length(source_ids_json) AS TotalSources,
                json_array_length(completed_ids_json) AS CompletedSources,
                retention_per_category IS NOT NULL AS RetentionPending
            FROM configuration_restore_operations ORDER BY rowid DESC LIMIT 1
            """);
        return new(pending?.Preview,pending?.Confirmed??false,job,GetResumeStepsForSetup(),legacy.GetStatus().Pending);
    }

    // Only step IDs, never credentials or source URLs, are exposed through setup state.
    internal int[]? GetResumeStepsForSetup()
    {
        if(setup.IsSetupCompleted()) return null;
        using var c=db.Open();
        if(c.ExecuteScalar<int>("SELECT COUNT(*) FROM configuration_restore_operations")==0)return null;
        try
        {
            var model=new ConfigurationSnapshotReader(protection,registry).Read(c,false,out _);
            var missing=ConfigurationSchema.MissingRequiredSecrets(model,registry);
            var steps=new List<int>();
            var security=model.Settings["security"].Deserialize<SecuritySettings>(ConfigurationSchema.Json)!;
            if(security.AuthMode!="open" && !SmartAuthPolicy.IsAuthConfigured(security,null)) steps.Add(3);
            if(model.ExternalProviders.Any(p=>missing.Any(m=>m.ItemKey==Text(p,"key"))))steps.Add(4);
            if(model.Sources.Count==0 && model.Providers.Count==0 || model.Providers.Any(p=>missing.Any(m=>m.ItemKey==Text(p,"key"))))steps.Add(5);
            if(model.Sources.Count==0 || model.Sources.Any(p=>missing.Any(m=>m.ItemKey==Text(p,"key"))))steps.Add(6);
            if(model.Applications.Any(p=>missing.Any(m=>m.ItemKey==Text(p,"key"))))steps.Add(7);
            steps.Add(8);return steps.ToArray();
        }
        catch(ConfigurationArchiveException){return [3,4,5,6,7,8];}
    }

    public Task CompleteRestoredSetupAsync(HttpContext caller,CancellationToken ct=default)
    {
        Authorize(caller);
        if(setup.IsSetupCompleted())throw Changed();
        return coordinator.RunExclusiveAsync("complete-restored-setup",null,cancellationToken=>
        {
            using var c=db.Open();
            string baseline;
            using(var read=c.BeginTransaction()) { baseline=ConfigurationRestorePlanner.Fingerprint(c,read); read.Commit(); }
            var model=new ConfigurationSnapshotReader(protection,registry).Read(c,false,out _);
            var security=model.Settings["security"].Deserialize<SecuritySettings>(ConfigurationSchema.Json)!;
            if(!ConfigurationSchema.Validate(model,registry).IsComplete || security.AuthMode!="open" && !SmartAuthPolicy.IsAuthConfigured(security,null))throw Changed();
            using var tx=c.BeginTransaction(deferred:false);
            if(ConfigurationRestorePlanner.Fingerprint(c,tx)!=baseline || c.ExecuteScalar<int>("SELECT COUNT(*) FROM configuration_restore_operations",transaction:tx)==0)throw Changed();
            var ui=ConfigurationRestorePlanner.Setting(c,tx,"ui",new UiSettings());ui.OnboardingDone=true;
            c.Execute("UPDATE app_settings SET value_json=@json WHERE key='ui'",new{json=JsonSerializer.Serialize(ui,ConfigurationSchema.Json)},tx);
            if(!ConfigurationRestorePlanner.HasLibrary(c,tx,HasMediaFiles()))
                c.Execute("UPDATE configuration_restore_operations SET initial_sync_state='pending',next_attempt_ts=0 WHERE id=(SELECT id FROM configuration_restore_operations ORDER BY rowid DESC LIMIT 1) AND initial_sync_state='held' AND retention_per_category IS NULL",transaction:tx);
            tx.Commit();Invalidate();return Task.FromResult(true);
        },ct);
    }

    private void Authorize(HttpContext caller)
    {
        if (caller.Items[BasicAuthMiddleware.AuthPassedKey] is not true ||
            !RequestForgeryProtection.HasTrustedNonBrowserHeader(caller.Request.Headers) &&
            !(caller.Request.Headers.TryGetValue("Origin", out var origin) && RequestForgeryProtection.IsAllowedOrigin(caller.Request, configuration, origin.ToString())))
            throw new BackupOperationException("configuration_restore_unauthorized", 403);
        if (!setup.IsSetupCompleted() && !SmartAuthPolicy.IsLoopbackRequest(caller) &&
            !SmartAuthPolicy.HasValidBootstrapSecretHeader(caller, SmartAuthPolicy.GetBootstrapSecret(configuration)) && caller.Items[BasicAuthMiddleware.BootstrapTokenActiveKey] is not true)
            throw new BackupOperationException("configuration_restore_bootstrap_required", 403);
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
    private void VerifyDestinationSecrets(ConfigurationDocument model)
    {
        foreach (var row in model.Providers.Concat(model.Sources)) Verify(row, "api_key");
        foreach (var row in model.Applications) Verify(row, "api_key_encrypted");
        foreach (var row in model.ExternalProviders)
        {
            registry.TryGet(Text(row, "provider_key"), out var definition);
            foreach (var field in definition.FieldsSchema) Verify((JsonObject)row["auth_json"]!, field.Key);
        }
        void Verify(JsonObject row, string field)
        {
            var value = Text(row, field);
            if (value.Length > 0 && (!protection.IsProtected(value) || !protection.TryUnprotect(value, out _)))
                throw new BackupOperationException("configuration_restore_credential_protection_failed", 422);
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
    private bool HasMediaFiles()
    {
        var posters = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(db.DbPath))!, "posters");
        return Directory.Exists(posters) && Directory.EnumerateFiles(posters, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }).Any();
    }
    private ConfigurationRestorePlan GetPlan(string id) => store.Read() is { } plan && plan.Id == id ? plan : throw Changed();
    private void Invalidate() { cache.Remove(SecuritySettingsCache.CacheKey); setup.Invalidate(); bootstrapTokens.InvalidateAll(); }
    private static BackupOperationException Changed() => new("configuration_restore_changed_or_invalid_state", 409);
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
    private sealed class InitialSyncJob
    {
        public string Id { get; set; } = ""; public string State { get; set; } = ""; public string Sources { get; set; } = "[]";
        public string Completed { get; set; } = "[]"; public long Next { get; set; }
        public int Attempts { get; set; }
    }
}

public sealed record ConfigurationRestoreState(ConfigurationRestorePreview? Pending,bool Confirmed,
    ConfigurationAppliedState? Applied,int[]? RemainingSetupSteps,bool LegacyRestorePending);

public sealed class ConfigurationAppliedState
{
    public string OperationId {get;set;}="";
    public string SyncState {get;set;}="";
    public int TotalSources {get;set;}
    public int CompletedSources {get;set;}
    public bool RetentionPending {get;set;}
}
