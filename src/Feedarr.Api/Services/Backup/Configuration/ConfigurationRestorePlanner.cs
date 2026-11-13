using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Services.Security;
using Microsoft.Data.Sqlite;

namespace Feedarr.Api.Services.Backup.Configuration;

internal static class ConfigurationRestorePlanner
{
    internal static ConfigurationRestorePlan Build(SqliteConnection conn, SqliteTransaction tx, ConfigurationDocument model, ConfigurationPreview preview, bool hasMediaFiles = false)
    {
        var providers = Match("providers", model.Providers, "base_url", "type");
        var sources = Match("sources", model.Sources, "torznab_url", null);
        var apps = Match("arr_applications", model.Applications, "base_url", "type");
        var external = new Dictionary<string, string?>(); var used = new HashSet<string>();
        var rows = conn.Query<(string Id, string Provider, string? Url, string? Name)>("SELECT instance_id,provider_key,base_url,display_name FROM external_provider_instances", transaction: tx).ToArray();
        foreach (var r in model.ExternalProviders)
        {
            var matches = rows.Where(x => x.Provider.Equals(Text(r, "provider_key"), StringComparison.OrdinalIgnoreCase) &&
                Canonical(x.Url) == Canonical(Text(r, "base_url")) && (x.Name ?? "") == Text(r, "display_name")).ToArray();
            if (matches.Length > 1 || matches.Length == 1 && !used.Add(matches[0].Id)) throw Ambiguous();
            external[Text(r, "key")] = matches.Length == 1 ? matches[0].Id : null;
        }
        foreach (var source in model.Sources)
        {
            if (sources[Text(source, "key")] is not long id || Text(source, "providerKey").Length == 0) continue;
            var type = conn.ExecuteScalar<string?>("SELECT p.type FROM sources s JOIN providers p ON p.id=s.provider_id WHERE s.id=@id", new { id }, tx);
            var incoming = model.Providers.Single(p => Text(p, "key") == Text(source, "providerKey"));
            if (type is not null && !type.Equals(Text(incoming, "type"), StringComparison.OrdinalIgnoreCase)) throw Ambiguous();
        }
        var general = Setting(conn, tx, "general", new GeneralSettings());
        var ui = Setting(conn, tx, "ui", new UiSettings());
        var security = Setting(conn, tx, "security", new SecuritySettings());
        var imported = model.Settings["general"].Deserialize<GeneralSettings>(ConfigurationSchema.Json)!;
        var incomingSecurity = model.Settings["security"].Deserialize<SecuritySettings>(ConfigurationSchema.Json)!;
        foreach (var limits in new[] { general, imported })
            if (limits.RssLimitPerCategory is < 1 or > 200 || limits.RssLimitGlobalPerSource is < 1 or > 2000 ||
                limits.RssLimitGlobalPerSource < limits.RssLimitPerCategory || limits.SyncIntervalMinutes is < 1 or > 1440)
                throw new BackupOperationException("configuration_restore_unsupported_retention_settings", 422);
        var hasLibrary = HasLibrary(conn, tx, hasMediaFiles);
        var fresh = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sources", transaction: tx) == 0 && !ui.OnboardingDone && !hasLibrary;
        var tighter = hasLibrary && (Tighter(imported.RssLimitPerCategory, general.RssLimitPerCategory) || Tighter(imported.RssLimitGlobalPerSource, general.RssLimitGlobalPerSource));
        var complete = preview.IsComplete && AuthenticationPolicy.IsAuthConfigured(incomingSecurity, null);
        var securityChange = security.RequireHttps != incomingSecurity.RequireHttps || security.Username != incomingSecurity.Username ||
            security.PasswordHash != incomingSecurity.PasswordHash || security.PasswordSalt != incomingSecurity.PasswordSalt || security.PublicBaseUrl != incomingSecurity.PublicBaseUrl;
        var op = Guid.NewGuid().ToString("N");
        var summary = new ConfigurationRestorePreview(op, fresh, hasLibrary, sources.Values.Count(x => x.HasValue), sources.Values.Count(x => !x.HasValue),
            conn.ExecuteScalar<int>("SELECT COUNT(DISTINCT source_id) FROM releases", transaction: tx) -
            sources.Values.Where(x => x.HasValue).Count(id => conn.ExecuteScalar<int>("SELECT EXISTS(SELECT 1 FROM releases WHERE source_id=@id)", new { id }, tx) == 1),
            tighter, securityChange, complete, preview);
        return new()
        {
            Id = op,
            Baseline = Fingerprint(conn, tx),
            Configuration = model,
            Providers = providers,
            Sources = sources,
            Applications = apps,
            ExternalProviders = external,
            Preview = summary,
            PreviousPerCategory = general.RssLimitPerCategory,
            PreviousGlobal = general.RssLimitGlobalPerSource
        };

        Dictionary<string, long?> Match(string table, List<JsonObject> items, string urlColumn, string? typeColumn)
        {
            var rows = conn.Query<(long Id, string Url, string Type)>($"SELECT id,{urlColumn}, {(typeColumn ?? "''")} FROM {table}", transaction: tx).ToArray();
            var result = new Dictionary<string, long?>(); var assigned = new HashSet<long>(); var incoming = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in items)
            {
                if (!incoming.Add((typeColumn is null ? "" : Text(r, typeColumn).ToLowerInvariant()) + "|" + Canonical(Text(r, urlColumn)))) throw Ambiguous();
                var matches = rows.Where(x => Canonical(x.Url) == Canonical(Text(r, urlColumn)) &&
                    (typeColumn is null || x.Type.Equals(Text(r, typeColumn), StringComparison.OrdinalIgnoreCase))).ToArray();
                if (matches.Length > 1 || matches.Length == 1 && !assigned.Add(matches[0].Id)) throw Ambiguous();
                result[Text(r, "key")] = matches.Length == 1 ? matches[0].Id : null;
            }
            return result;
        }
    }
    internal static bool HasLibrary(SqliteConnection conn, SqliteTransaction tx, bool hasMediaFiles) => hasMediaFiles ||
        conn.ExecuteScalar<int>("SELECT EXISTS(SELECT 1 FROM releases) OR EXISTS(SELECT 1 FROM media_entities) OR EXISTS(SELECT 1 FROM poster_store_refs)", transaction: tx) == 1;

    internal static string Fingerprint(SqliteConnection conn, SqliteTransaction tx)
    {
        var queries = new[]{
            "SELECT key,value_json FROM app_settings ORDER BY key",
            "SELECT id,type,name,base_url,api_key_encrypted,enabled FROM providers ORDER BY id",
            "SELECT id,name,enabled,torznab_url,api_key,auth_mode,rss_mode,provider_id,color FROM sources ORDER BY id",
            "SELECT * FROM arr_applications ORDER BY id",
            "SELECT * FROM external_provider_instances ORDER BY instance_id",
            "SELECT source_id,cat_id,name,parent_cat_id,is_sub,unified_key,unified_label FROM source_categories ORDER BY source_id,cat_id",
            "SELECT source_id,cat_id,group_key,group_label FROM source_category_mappings ORDER BY source_id,cat_id",
            "SELECT source_id,cat_id FROM source_selected_categories ORDER BY source_id,cat_id"};
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var sql in queries)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(conn.Query(sql, transaction: tx).ToArray());
            try { hash.AppendData(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    internal static T Setting<T>(SqliteConnection c, SqliteTransaction tx, string key, T fallback)
    {
        var json = c.ExecuteScalar<string?>("SELECT value_json FROM app_settings WHERE key=@key", new { key }, tx);
        if (json is null) return fallback;
        if (typeof(T) == typeof(SecuritySettings))
        {
            var document = JsonNode.Parse(json)!.AsObject(); AuthenticationPolicy.NormalizeSecurityDocument(document);
            return document.Deserialize<T>(ConfigurationSchema.Json) ?? fallback;
        }
        return JsonSerializer.Deserialize<T>(json, ConfigurationSchema.Json) ?? fallback;
    }
    private static bool Tighter(int next, int old) => next > 0 && (old <= 0 || next < old);
    private static string Canonical(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw Ambiguous();
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + uri.AbsolutePath.TrimEnd('/') + uri.Query;
    }
    private static string Text(JsonObject r, string key) => ConfigurationSchema.Text(r, key);
    private static BackupOperationException Ambiguous() => new("configuration_restore_ambiguous_identity", 409);
}
