using System.Text.Json;
using System.Text.Json.Nodes;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Security;
using Microsoft.Data.Sqlite;

namespace Feedarr.Api.Services.Backup.Configuration;

/// <summary>Read-only allowlisted projection. No repositories with lazy migration writes are called.</summary>
internal sealed class ConfigurationSnapshotReader(IApiKeyProtectionService protection, ExternalProviderRegistry registry)
{
    internal ConfigurationDocument Read(SqliteConnection conn, bool legacy, out IReadOnlyList<ConfigurationSecretRequirement> unreadableSecrets)
    {
        var unreadable = new List<ConfigurationSecretRequirement>();
        long textBytes = 0;
        int rowCount = 0;
        void Budget(string value)
        {
            textBytes += System.Text.Encoding.UTF8.GetByteCount(value);
            if (textBytes > ConfigurationArchiveService.MaxConfigurationBytes) TooLarge();
        }
        using var tx = conn.BeginTransaction(deferred: true);
        var settings = new Dictionary<string, JsonObject>();
        var rawSettings = new Dictionary<string, string>();
        if (HasTable("app_settings"))
        {
            using var command = conn.CreateCommand(); command.Transaction = tx;
            command.CommandText = "SELECT key,value_json FROM app_settings LIMIT 10001";
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                if (rawSettings.Count >= ConfigurationSchema.MaxRows) TooLarge();
                var key = rows.GetString(0); var value = rows.GetString(1);
                Budget(key); Budget(value);
                rawSettings.Add(key, value);
            }
        }
        settings["general"] = Setting("general", new GeneralSettings());
        settings["ui"] = Setting("ui", new UiSettings());
        settings["ui"].Remove("onboardingDone");
        settings["arr"] = Setting("arr", settings["general"].Deserialize<ArrSettings>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ArrSettings());
        // Legacy GeneralSettings also carried Arr fields. A dedicated section wins.
        foreach (var name in new[] { "arrSyncIntervalMinutes", "arrAutoSyncEnabled", "requestIntegrationMode" })
            settings["general"][name] = settings["arr"][name]?.DeepClone();
        settings["maintenance"] = Setting("maintenance", new MaintenanceSettings());
        settings["security"] = Setting("security", new SecuritySettings());

        var providerMap = new Dictionary<long, string>();
        var providers = ReadRows("providers", ConfigurationSchema.ProviderFields, providerMap);
        var sourceMap = new Dictionary<long, string>();
        var sources = ReadRows("sources", ConfigurationSchema.SourceFields, sourceMap);
        var appMap = new Dictionary<long, string>();
        var apps = ReadRows("arr_applications", ConfigurationSchema.ApplicationFields, appMap);
        var external = ReadRows("external_provider_instances", ConfigurationSchema.ExternalFields);
        var categories = ReadRows("source_categories", ConfigurationSchema.CategoryFields);
        var mappings = ReadRows("source_category_mappings", ConfigurationSchema.MappingFields);
        var selections = ReadRows("source_selected_categories", ConfigurationSchema.SelectionFields);
        foreach (var r in sources) Remap(r, "providerKey", providerMap);
        foreach (var rows in new[] { categories, mappings, selections }) foreach (var r in rows) Remap(r, "sourceKey", sourceMap);
        // Keep definitions needed to explain configured selections/mappings and
        // their ancestors, not the complete reconstructible CAPS category cache.
        var definitions = categories.ToDictionary(r => (ConfigurationSchema.Text(r, "sourceKey"), r["cat_id"]!.GetValue<long>()));
        var needed = mappings.Concat(selections).Select(r => (ConfigurationSchema.Text(r, "sourceKey"), r["cat_id"]!.GetValue<long>())).ToHashSet();
        var pending = new Queue<(string, long)>(needed);
        while (pending.TryDequeue(out var identity))
            if (definitions.TryGetValue(identity, out var row) && row["parent_cat_id"] is not null)
            {
                var parent = (identity.Item1, row["parent_cat_id"]!.GetValue<long>());
                if (parent.Item2 > 0 && needed.Add(parent)) pending.Enqueue(parent);
            }
        categories.RemoveAll(r => !needed.Contains((ConfigurationSchema.Text(r, "sourceKey"), r["cat_id"]!.GetValue<long>())));
        RemapUi("defaultFilterSourceId", sourceMap, []);
        RemapUi("defaultFilterApplication", appMap, ["__hide_apps__"]);
        foreach (var p in providers) p["api_key"] = Secret(ConfigurationSchema.Text(p, "api_key"), ConfigurationSchema.Text(p, "key"), "api_key");
        foreach (var s in sources) s["api_key"] = Secret(ConfigurationSchema.Text(s, "api_key"), ConfigurationSchema.Text(s, "key"), "api_key");
        foreach (var a in apps) a["api_key_encrypted"] = Secret(ConfigurationSchema.Text(a, "api_key_encrypted"), ConfigurationSchema.Text(a, "key"), "api_key_encrypted");
        foreach (var e in external)
        {
            if (!registry.TryGet(ConfigurationSchema.Text(e, "provider_key"), out var definition)) ConfigurationSchema.Fail();
            var auth = (JsonObject)e["auth_json"]!;
            foreach (var f in definition.FieldsSchema)
                if (auth[f.Key] is not null && (f.Secret || protection.IsProtected(auth[f.Key]!.GetValue<string>())))
                    auth[f.Key] = Secret(auth[f.Key]!.GetValue<string>(), ConfigurationSchema.Text(e, "key"), f.Key);
        }
        // Existing instances are authoritative per provider. Never merge stale legacy
        // credentials into them, even when an authoritative instance is disabled.
        var old = rawSettings.TryGetValue("external", out var oldJson) ? ConfigurationArchiveService.ParseObject(oldJson) : new JsonObject();
        foreach (var (provider, fields) in new[]{
            ("tmdb",new[]{("apiKey","tmdb_api_key","tmdbApiKey")}),
            ("tvmaze",new[]{("apiKey","tvmaze_api_key","tvmazeApiKey")}),
            ("fanart",new[]{("apiKey","fanart_api_key","fanartApiKey")}),
            ("igdb",new[]{("clientId","igdb_client_id","igdbClientId"),("clientSecret","igdb_client_secret","igdbClientSecret")})})
        {
            if (external.Any(e => ConfigurationSchema.Text(e, "provider_key").Equals(provider, StringComparison.OrdinalIgnoreCase))) continue;
            var auth = new JsonObject();
            var logicalKey = NewKey();
            foreach (var (field, key, oldKey) in fields)
            {
                var value = rawSettings.TryGetValue(key, out var raw) ? ReadString(raw) : old[oldKey]?.GetValue<string>();
                auth[field] = Secret(value ?? "", logicalKey, field);
            }
            var enabledKey = provider + "_enabled";
            bool? enabled = null;
            if (rawSettings.TryGetValue(enabledKey, out var flag)) enabled = ReadString(flag) != "0";
            else if (old[provider + "Enabled"] is not null) enabled = old[provider + "Enabled"]!.GetValue<bool>();
            if (!auth.Any(f => !string.IsNullOrEmpty(f.Value?.GetValue<string>())) && enabled is null) continue;
            registry.TryGet(provider, out var definition);
            external.Add(new JsonObject
            {
                ["key"] = logicalKey,
                ["provider_key"] = provider,
                ["display_name"] = null,
                ["enabled"] = enabled == false ? 0L : 1L,
                ["base_url"] = definition.DefaultBaseUrl,
                ["auth_json"] = auth,
                ["options_json"] = new JsonObject(),
                ["priority"] = 0L
            });
        }
        tx.Commit();
        unreadableSecrets = unreadable;
        return new ConfigurationDocument
        {
            SchemaVersion = ConfigurationSchema.Version,
            Settings = settings,
            Providers = providers,
            Sources = sources,
            ExternalProviders = external,
            Applications = apps,
            Categories = categories,
            Mappings = mappings,
            Selections = selections
        };

        bool HasTable(string table)
        {
            using var cmd = conn.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "SELECT type,sql FROM sqlite_schema WHERE name=$name"; cmd.Parameters.AddWithValue("$name", table);
            using var row = cmd.ExecuteReader();
            if (!row.Read()) { if (!legacy) ConfigurationSchema.Fail(); return false; }
            if (row.GetString(0) != "table" || row.GetString(1).Contains("VIRTUAL", StringComparison.OrdinalIgnoreCase)) ConfigurationSchema.Fail();
            return true;
        }
        JsonObject Setting<T>(string name, T fallback) => rawSettings.TryGetValue(name, out var value)
            ? ConfigurationArchiveService.ParseObject(value)
            : JsonSerializer.SerializeToNode(fallback, ConfigurationSchema.Json)!.AsObject();
        string Secret(string value, string itemKey, string field)
        {
            if (value.Length == 0) return "";
            if (protection.TryUnprotect(value, out var plain) && plain is not null) return plain;
            unreadable.Add(new(itemKey, field));
            if (!legacy) throw new ConfigurationArchiveException(ConfigurationArchiveError.UnreadableSecrets);
            return "";
        }
        void Remap(JsonObject row, string field, Dictionary<long, string> map)
        {
            if (row[field] is null) return;
            if (!map.TryGetValue(row[field]!.GetValue<long>(), out var key)) ConfigurationSchema.Fail();
            row[field] = key;
        }
        void RemapUi(string field, Dictionary<long, string> map, string[] special)
        {
            var value = settings["ui"][field]?.GetValue<string>() ?? "";
            settings["ui"][field] = special.Contains(value) ? value :
                long.TryParse(value, out var id) && map.TryGetValue(id, out var key) ? key : "";
        }
        List<JsonObject> ReadRows(string table, Dictionary<string, string> fields, Dictionary<long, string>? map = null)
        {
            var result = new List<JsonObject>();
            if (!HasTable(table)) return result;
            var columns = new HashSet<string>(StringComparer.Ordinal);
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx; cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
                using var reader = cmd.ExecuteReader(); while (reader.Read()) columns.Add(reader.GetString(1));
            }
            var select = new List<string>();
            if (map is not null) select.Add("id AS __id");
            foreach (var (field, kind) in fields)
            {
                if (field == "key") continue;
                var column = field switch { "sourceKey" => "source_id", "providerKey" => "provider_id", _ => field };
                if (column == "api_key" && table == "providers" && columns.Contains("api_key_encrypted")) column = "api_key_encrypted";
                var fallback = kind switch { "b" => field is "search_cutoff" ? "0" : "1", "i" => "0", "o" => "'{}'", "s" => field == "auth_mode" ? "'query'" : "''", _ => "NULL" };
                select.Add($"{(columns.Contains(column) ? $"\"{column}\"" : fallback)} AS \"{field}\"");
            }
            using var command = conn.CreateCommand(); command.Transaction = tx;
            var order = table == "external_provider_instances" && columns.Contains("updated_at_ts") && columns.Contains("created_at_ts")
                ? " ORDER BY updated_at_ts DESC, created_at_ts DESC" : "";
            command.CommandText = $"SELECT {string.Join(',', select)} FROM \"{table}\"{order} LIMIT 10001";
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                if (++rowCount > ConfigurationSchema.MaxRows) TooLarge();
                var row = new JsonObject();
                if (fields.ContainsKey("key"))
                {
                    var key = NewKey(); row["key"] = key;
                    if (map is not null) map.Add(rows.GetInt64(rows.GetOrdinal("__id")), key);
                }
                foreach (var (name, kind) in fields)
                {
                    if (name == "key") continue;
                    var ordinal = rows.GetOrdinal(name);
                    if (!rows.IsDBNull(ordinal) && rows.GetValue(ordinal) is string text) Budget(text);
                    row[name] = rows.IsDBNull(ordinal) ? null : kind == "o"
                        ? ConfigurationArchiveService.ParseObject(rows.GetString(ordinal))
                        : JsonSerializer.SerializeToNode(rows.GetValue(ordinal));
                }
                if (table == "external_provider_instances") row["priority"] = (long)result.Count;
                result.Add(row);
            }
            return result;
        }
    }
    private static string? ReadString(string raw)
    {
        try { return JsonSerializer.Deserialize<string>(raw); }
        catch (JsonException) { return raw.Trim().Trim('"'); } // supported pre-migration settings
    }
    private static string NewKey() => "ref-" + Guid.NewGuid().ToString("N");
    private static void TooLarge() => throw new ConfigurationArchiveException(ConfigurationArchiveError.SizeLimitExceeded);
}
