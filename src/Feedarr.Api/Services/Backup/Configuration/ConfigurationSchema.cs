using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.ExternalProviders;

namespace Feedarr.Api.Services.Backup.Configuration;

internal static class ConfigurationSchema
{
    internal const int Version = 1;
    internal const int MaxRows = 10_000;
    internal const int MaxStringLength = 16_384;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    // Explicit allowlists: never SELECT * or copy operational fields into the archive.
    // ? = nullable string, s = string, i = integer, b = SQLite 0/1 boolean.
    internal static readonly Dictionary<string, string> ProviderFields = Fields("key:s type:s name:s base_url:s api_key:? enabled:b");
    internal static readonly Dictionary<string, string> SourceFields = Fields("key:s providerKey:? name:s enabled:b torznab_url:s api_key:? auth_mode:s rss_mode:? color:?");
    internal static readonly Dictionary<string, string> ExternalFields = Fields("key:s provider_key:s display_name:? enabled:b base_url:? auth_json:o options_json:o priority:i");
    internal static readonly Dictionary<string, string> ApplicationFields = Fields("key:s type:s name:? base_url:s api_key_encrypted:? is_enabled:b is_default:b root_folder_path:? quality_profile_id:n tags:? series_type:? season_folder:b monitor_mode:? search_missing:b search_cutoff:b minimum_availability:? search_for_movie:b");
    internal static readonly Dictionary<string, string> CategoryFields = Fields("sourceKey:s cat_id:i name:s parent_cat_id:n is_sub:b unified_key:? unified_label:?");
    internal static readonly Dictionary<string, string> MappingFields = Fields("sourceKey:s cat_id:i group_key:? group_label:?");
    internal static readonly Dictionary<string, string> SelectionFields = Fields("sourceKey:s cat_id:i");
    internal static readonly string[] SettingNames = ["general", "ui", "arr", "maintenance", "security"];
    private static Dictionary<string, string> Fields(string fields) => fields.Split(' ').Select(x => x.Split(':')).ToDictionary(x => x[0], x => x[1]);
    internal static void Fail() => throw new ConfigurationArchiveException(ConfigurationArchiveError.InvalidConfiguration);
    internal static string Text(JsonObject row, string key) => row[key]?.GetValue<string>() ?? "";

    internal static ConfigurationPreview Validate(ConfigurationDocument model, ExternalProviderRegistry registry)
    {
        if (model.SchemaVersion != Version) throw new ConfigurationArchiveException(ConfigurationArchiveError.UnsupportedVersion);
        if (model.Settings is null || !model.Settings.Keys.Order().SequenceEqual(SettingNames.Order())) Fail();
        if (model.Settings!.Values.Any(x => x is null)) Fail();
        ValidateSettings<GeneralSettings>(model.Settings["general"]);
        ValidateSettings<ArrSettings>(model.Settings["arr"]);
        ValidateSettings<MaintenanceSettings>(model.Settings["maintenance"]);
        AuthenticationPolicy.NormalizeSecurityDocument(model.Settings["security"]);
        ValidateSettings<SecuritySettings>(model.Settings["security"]);
        ValidateSettings<UiSettings>(model.Settings["ui"]);
        if (model.Settings["ui"].ContainsKey("onboardingDone")) Fail();
        ValidateRows(model.Providers, ProviderFields);
        ValidateRows(model.Sources, SourceFields);
        ValidateRows(model.ExternalProviders, ExternalFields);
        ValidateRows(model.Applications, ApplicationFields);
        ValidateRows(model.Categories, CategoryFields);
        ValidateRows(model.Mappings, MappingFields);
        ValidateRows(model.Selections, SelectionFields);
        if (new[] { model.Providers, model.Sources, model.ExternalProviders, model.Applications, model.Categories, model.Mappings, model.Selections }.Sum(x => x.Count) > MaxRows)
            throw new ConfigurationArchiveException(ConfigurationArchiveError.SizeLimitExceeded);
        var providers = UniqueKeys(model.Providers);
        var sources = UniqueKeys(model.Sources);
        var apps = UniqueKeys(model.Applications);
        _ = UniqueKeys(model.ExternalProviders);
        foreach (var p in model.Providers)
        {
            if (Text(p, "type") is not ("jackett" or "prowlarr")) Fail();
            Url(Text(p, "base_url"));
        }
        foreach (var s in model.Sources)
        {
            var provider = Text(s, "providerKey");
            if (provider.Length > 0 && !providers.Contains(provider)) Fail();
            if (Text(s, "auth_mode") is not ("query" or "header")) Fail();
            Url(Text(s, "torznab_url"));
        }
        if (model.Sources.GroupBy(x => Text(x, "torznab_url"), StringComparer.Ordinal).Any(g => g.Count() > 1)) Fail();
        foreach (var a in model.Applications)
        {
            if (Text(a, "type") is not ("sonarr" or "radarr" or "overseerr" or "jellyseerr" or "seer")) Fail();
            Url(Text(a, "base_url"));
        }
        if (model.Applications.Where(x => x["is_default"]!.GetValue<long>() == 1).GroupBy(x => Text(x, "type")).Any(g => g.Count() > 1)) Fail();
        foreach (var rows in new[] { model.Categories, model.Mappings, model.Selections })
        {
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in rows)
            {
                if (!sources.Contains(Text(r, "sourceKey")) || r["cat_id"]!.GetValue<long>() <= 0 ||
                    !identities.Add(Text(r, "sourceKey") + ":" + r["cat_id"])) Fail();
            }
        }
        foreach (var m in model.Mappings)
            if (Text(m, "group_key") is not ("" or "films" or "series" or "animation" or "anime" or "games" or "comics" or "books" or "audio" or "spectacle" or "emissions")) Fail();
        foreach (var e in model.ExternalProviders)
        {
            if (!registry.TryGet(Text(e, "provider_key"), out var definition)) Fail();
            if (Text(e, "base_url").Length > 0) Url(Text(e, "base_url"));
            var auth = (JsonObject)e["auth_json"]!;
            foreach (var field in auth)
                if (!definition.FieldsSchema.Any(f => f.Key == field.Key) || (field.Value is not null && field.Value.GetValueKind() != JsonValueKind.String)) Fail();
        }
        foreach (var group in model.ExternalProviders.GroupBy(x => Text(x, "provider_key"), StringComparer.OrdinalIgnoreCase))
            if (group.Any(x => x["priority"]!.GetValue<long>() < 0) || group.Select(x => x["priority"]!.GetValue<long>()).Distinct().Count() != group.Count()) Fail();
        ValidateUiReference(model.Settings["ui"], "defaultFilterSourceId", sources, []);
        ValidateUiReference(model.Settings["ui"], "defaultFilterApplication", apps, ["__hide_apps__"]);
        foreach (var section in model.Settings.Values) CheckStrings(section);
        foreach (var rows in new[] { model.Providers, model.Sources, model.ExternalProviders, model.Applications, model.Categories, model.Mappings, model.Selections })
            foreach (var row in rows) CheckStrings(row);
        var security = model.Settings["security"].Deserialize<SecuritySettings>(Json)!;
        if (security.PasswordHash.Length > 0 || security.PasswordSalt.Length > 0)
        {
            try { if (Convert.FromBase64String(security.PasswordHash).Length != 32 || Convert.FromBase64String(security.PasswordSalt).Length != 16) Fail(); }
            catch (FormatException) { Fail(); }
        }
        var missing = MissingRequiredSecrets(model, registry).Count;
        return new(model.Sources.Count, model.Sources.Count, model.Providers.Count, model.ExternalProviders.Count,
            model.Applications.Count, model.Categories.Count, model.Mappings.Count, model.Selections.Count,
            model.Settings.Count, model.Settings.Sum(x => x.Value.Count), missing, missing == 0 && model.Sources.Count > 0);
    }

    internal static List<ConfigurationSecretRequirement> MissingRequiredSecrets(ConfigurationDocument model, ExternalProviderRegistry registry)
    {
        var result = new List<ConfigurationSecretRequirement>();
        foreach (var p in model.Providers.Where(x => IsEnabled(x, "enabled") && Text(x, "api_key").Length == 0))
            result.Add(new(Text(p, "key"), "api_key"));
        foreach (var s in model.Sources.Where(x => IsEnabled(x, "enabled") && Text(x, "api_key").Length == 0 &&
            !model.Providers.Any(p => Text(p, "key") == Text(x, "providerKey") && Text(p, "api_key").Length > 0)))
            result.Add(new(Text(s, "key"), "api_key"));
        foreach (var a in model.Applications.Where(x => IsEnabled(x, "is_enabled") && Text(x, "api_key_encrypted").Length == 0))
            result.Add(new(Text(a, "key"), "api_key_encrypted"));
        foreach (var e in model.ExternalProviders.Where(x => IsEnabled(x, "enabled")))
        {
            registry.TryGet(Text(e, "provider_key"), out var definition);
            foreach (var field in definition.FieldsSchema.Where(f => f.Required && string.IsNullOrWhiteSpace(e["auth_json"]![f.Key]?.GetValue<string>())))
                result.Add(new(Text(e, "key"), field.Key));
        }
        var security = model.Settings["security"].Deserialize<SecuritySettings>(Json)!;
        if (!AuthenticationPolicy.IsAuthConfigured(security, null))
            result.Add(new("security", "basicCredentials"));
        return result;
    }

    private static bool IsEnabled(JsonObject row, string key) => row[key]!.GetValue<long>() == 1;
    private static void ValidateSettings<T>(JsonObject node)
    {
        try
        {
            var settings = node.Deserialize<T>(Json);
            if (settings is null || typeof(T).GetProperties().Any(p => !p.PropertyType.IsValueType && p.GetValue(settings) is null)) Fail();
        }
        catch (JsonException) { Fail(); }
    }
    private static void ValidateUiReference(JsonObject ui, string field, HashSet<string> keys, string[] special)
    {
        var value = ui[field]?.GetValue<string>() ?? "";
        if (value.Length > 0 && !keys.Contains(value) && !special.Contains(value)) Fail();
    }
    private static HashSet<string> UniqueKeys(List<JsonObject> rows)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = Text(row, "key");
            if (key.Length is < 1 or > 80 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') || !keys.Add(key)) Fail();
        }
        return keys;
    }
    private static void ValidateRows(List<JsonObject>? rows, Dictionary<string, string> fields)
    {
        if (rows is null || rows.Count > MaxRows) Fail();
        foreach (var row in rows!)
        {
            if (row is null || !row.Select(x => x.Key).Order().SequenceEqual(fields.Keys.Order())) Fail();
            foreach (var (name, type) in fields)
            {
                var value = row![name];
                if (value is null) { if (type is not ("?" or "n")) Fail(); continue; }
                var kind = value.GetValueKind();
                if (type is "s" or "?" && kind != JsonValueKind.String) Fail();
                if (type is "i" or "n" or "b")
                {
                    if (kind != JsonValueKind.Number || !value.AsValue().TryGetValue<long>(out var number)) Fail();
                    if (type == "b" && value.GetValue<long>() is not (0 or 1)) Fail();
                }
                if (type == "o" && kind != JsonValueKind.Object) Fail();
            }
        }
    }
    private static void Url(string value)
    {
        // Structural validation only. Destination SSRF policy must be reapplied by lot 2 before use.
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0) Fail();
    }
    private static void CheckStrings(JsonNode node)
    {
        if (node is JsonObject o) foreach (var p in o) { if (p.Key.Length > 128) Fail(); if (p.Value is not null) CheckStrings(p.Value); }
        else if (node is JsonArray a) foreach (var v in a) { if (v is not null) CheckStrings(v); }
        else if (node.GetValueKind() == JsonValueKind.String && node.GetValue<string>().Length > MaxStringLength) Fail();
    }
}
