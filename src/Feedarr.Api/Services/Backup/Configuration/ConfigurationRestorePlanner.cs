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
    internal static ConfigurationRestorePlan Build(SqliteConnection conn, SqliteTransaction tx, ConfigurationDocument model)
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
            external[Text(r, "key")] = matches.Length == 1 && used.Add(matches[0].Id) ? matches[0].Id : null;
        }
        foreach (var source in model.Sources)
        {
            if (sources[Text(source, "key")] is not long id || Text(source, "providerKey").Length == 0) continue;
            var type = conn.ExecuteScalar<string?>("SELECT p.type FROM sources s JOIN providers p ON p.id=s.provider_id WHERE s.id=@id", new { id }, tx);
            var incoming = model.Providers.Single(p => Text(p, "key") == Text(source, "providerKey"));
            if (type is not null && !type.Equals(Text(incoming, "type"), StringComparison.OrdinalIgnoreCase)) throw Ambiguous();
        }
        return new(providers, sources, apps, external);

        Dictionary<string, long?> Match(string table, List<JsonObject> items, string urlColumn, string? typeColumn)
        {
            var rows = conn.Query<(long Id, string Url, string Type, string? Name)>($"SELECT id,{urlColumn}, {(typeColumn ?? "''")},name FROM {table}", transaction: tx).ToArray();
            var result = new Dictionary<string, long?>(); var assigned = new HashSet<long>();
            foreach (var r in items)
            {
                var matches = rows.Where(x => Canonical(x.Url) == Canonical(Text(r, urlColumn)) &&
                    (typeColumn is null || x.Type.Equals(Text(r, typeColumn), StringComparison.OrdinalIgnoreCase))).ToArray();
                if (matches.Length > 1) matches = matches.Where(x => typeColumn is null ? x.Url == Text(r, urlColumn) : (x.Name ?? "") == Text(r, "name")).ToArray();
                result[Text(r, "key")] = matches.Length == 1 && assigned.Add(matches[0].Id) ? matches[0].Id : null;
            }
            return result;
        }
    }
    internal static string Fingerprint(SqliteConnection conn, SqliteTransaction tx)
    {
        var queries = new[]{
            "SELECT key,value_json FROM app_settings WHERE key IN ('general','ui','arr','maintenance') ORDER BY key",
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
        return JsonSerializer.Deserialize<T>(json, ConfigurationSchema.Json) ?? fallback;
    }
    private static string Canonical(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw Ambiguous();
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + uri.AbsolutePath.TrimEnd('/') + uri.Query;
    }
    private static string Text(JsonObject r, string key) => ConfigurationSchema.Text(r, key);
    private static BackupOperationException Ambiguous() => new("configuration_restore_ambiguous_identity", 409);
}

internal sealed record ConfigurationRestorePlan(Dictionary<string, long?> Providers, Dictionary<string, long?> Sources,
    Dictionary<string, long?> Applications, Dictionary<string, string?> ExternalProviders);
