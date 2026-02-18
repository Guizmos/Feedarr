using System.Text.Json;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Services.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Feedarr.Api.Data.Repositories;

public sealed class SettingsRepository
{
    private readonly Db _db;
    private readonly IApiKeyProtectionService _keyProtection;
    private readonly ILogger<SettingsRepository> _logger;

    private static readonly string[] ExternalApiKeySettingKeys =
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

    public SettingsRepository(Db db)
        : this(db, new PassthroughApiKeyProtectionService(), NullLogger<SettingsRepository>.Instance)
    {
    }

    public SettingsRepository(
        Db db,
        IApiKeyProtectionService keyProtection,
        ILogger<SettingsRepository> logger)
    {
        _db = db;
        _keyProtection = keyProtection;
        _logger = logger;
    }

    // --------------------
    // GENERAL
    // --------------------
    public GeneralSettings GetGeneral(GeneralSettings defaults)
    {
        var json = GetRaw("general");
        if (string.IsNullOrWhiteSpace(json)) return defaults;

        try
        {
            var loaded = JsonSerializer.Deserialize<GeneralSettings>(json, JsonOpts);
            if (loaded is null) return defaults;

            defaults.SyncIntervalMinutes = loaded.SyncIntervalMinutes != 0 ? loaded.SyncIntervalMinutes : defaults.SyncIntervalMinutes;
            defaults.RssLimit = loaded.RssLimit != 0 ? loaded.RssLimit : defaults.RssLimit;
            defaults.RssLimitPerCategory = loaded.RssLimitPerCategory != 0
                ? loaded.RssLimitPerCategory
                : (loaded.RssLimit != 0 ? loaded.RssLimit : defaults.RssLimitPerCategory);
            defaults.RssLimitGlobalPerSource = loaded.RssLimitGlobalPerSource != 0
                ? loaded.RssLimitGlobalPerSource
                : defaults.RssLimitGlobalPerSource;
            if (json.Contains("autoSyncEnabled", StringComparison.OrdinalIgnoreCase))
                defaults.AutoSyncEnabled = loaded.AutoSyncEnabled;
            if (json.Contains("arrSyncIntervalMinutes", StringComparison.OrdinalIgnoreCase) &&
                loaded.ArrSyncIntervalMinutes > 0)
            {
                defaults.ArrSyncIntervalMinutes = loaded.ArrSyncIntervalMinutes;
            }
            if (json.Contains("arrAutoSyncEnabled", StringComparison.OrdinalIgnoreCase))
                defaults.ArrAutoSyncEnabled = loaded.ArrAutoSyncEnabled;
            if (json.Contains("requestIntegrationMode", StringComparison.OrdinalIgnoreCase))
            {
                var mode = (loaded.RequestIntegrationMode ?? "arr").Trim().ToLowerInvariant();
                defaults.RequestIntegrationMode = mode is "overseerr" or "jellyseerr" or "seer" ? mode : "arr";
            }
            return defaults;
        }
        catch
        {
            return defaults;
        }
    }

    public void SaveGeneral(GeneralSettings settings)
        => Upsert("general", JsonSerializer.Serialize(settings, JsonOpts));

    // --------------------
    // UI
    // --------------------
    public UiSettings GetUi(UiSettings defaults)
    {
        var json = GetRaw("ui");
        if (string.IsNullOrWhiteSpace(json)) return defaults;

        try
        {
            var loaded = JsonSerializer.Deserialize<UiSettings>(json, JsonOpts);
            if (loaded is null) return defaults;

            defaults.HideSeenByDefault = loaded.HideSeenByDefault;
            defaults.ShowCategories = loaded.ShowCategories;
            if (json.Contains("enableMissingPosterView", StringComparison.OrdinalIgnoreCase))
                defaults.EnableMissingPosterView = loaded.EnableMissingPosterView;
            defaults.DefaultView = string.IsNullOrWhiteSpace(loaded.DefaultView) ? defaults.DefaultView : loaded.DefaultView;
            if (json.Contains("badgeInfo", StringComparison.OrdinalIgnoreCase))
                defaults.BadgeInfo = loaded.BadgeInfo;
            if (json.Contains("badgeWarn", StringComparison.OrdinalIgnoreCase))
                defaults.BadgeWarn = loaded.BadgeWarn;
            if (json.Contains("badgeError", StringComparison.OrdinalIgnoreCase))
                defaults.BadgeError = loaded.BadgeError;
            if (json.Contains("theme", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(loaded.Theme))
                defaults.Theme = loaded.Theme;
            if (json.Contains("animationsEnabled", StringComparison.OrdinalIgnoreCase))
                defaults.AnimationsEnabled = loaded.AnimationsEnabled;
            if (json.Contains("onboardingDone", StringComparison.OrdinalIgnoreCase))
                defaults.OnboardingDone = loaded.OnboardingDone;
            if (json.Contains("defaultSort", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(loaded.DefaultSort))
                defaults.DefaultSort = loaded.DefaultSort;
            if (json.Contains("defaultMaxAgeDays", StringComparison.OrdinalIgnoreCase))
                defaults.DefaultMaxAgeDays = loaded.DefaultMaxAgeDays;
            if (json.Contains("defaultLimit", StringComparison.OrdinalIgnoreCase))
                defaults.DefaultLimit = loaded.DefaultLimit;
            if (json.Contains("defaultFilterSeen", StringComparison.OrdinalIgnoreCase))
                defaults.DefaultFilterSeen = loaded.DefaultFilterSeen;
            if (json.Contains("defaultFilterApplication", StringComparison.OrdinalIgnoreCase))
                defaults.DefaultFilterApplication = loaded.DefaultFilterApplication;
            if (json.Contains("defaultFilterSourceId", StringComparison.OrdinalIgnoreCase))
                defaults.DefaultFilterSourceId = loaded.DefaultFilterSourceId;
            if (json.Contains("defaultFilterCategoryId", StringComparison.OrdinalIgnoreCase))
                defaults.DefaultFilterCategoryId = loaded.DefaultFilterCategoryId;
            if (json.Contains("defaultFilterQuality", StringComparison.OrdinalIgnoreCase))
                defaults.DefaultFilterQuality = loaded.DefaultFilterQuality;
            return defaults;
        }
        catch
        {
            return defaults;
        }
    }

    public void SaveUi(UiSettings settings)
        => Upsert("ui", JsonSerializer.Serialize(settings, JsonOpts));

    // --------------------
    // SECURITY
    // --------------------
    public SecuritySettings GetSecurity(SecuritySettings defaults)
    {
        var json = GetRaw("security");
        if (string.IsNullOrWhiteSpace(json)) return defaults;

        try
        {
            var loaded = JsonSerializer.Deserialize<SecuritySettings>(json, JsonOpts);
            if (loaded is null) return defaults;

            if (!string.IsNullOrWhiteSpace(loaded.Authentication))
                defaults.Authentication = loaded.Authentication;
            if (!string.IsNullOrWhiteSpace(loaded.AuthenticationRequired))
                defaults.AuthenticationRequired = loaded.AuthenticationRequired;
            if (json.Contains("username", StringComparison.OrdinalIgnoreCase))
                defaults.Username = loaded.Username ?? defaults.Username;
            if (json.Contains("passwordHash", StringComparison.OrdinalIgnoreCase))
                defaults.PasswordHash = loaded.PasswordHash ?? defaults.PasswordHash;
            if (json.Contains("passwordSalt", StringComparison.OrdinalIgnoreCase))
                defaults.PasswordSalt = loaded.PasswordSalt ?? defaults.PasswordSalt;

            return defaults;
        }
        catch
        {
            return defaults;
        }
    }

    public void SaveSecurity(SecuritySettings settings)
        => Upsert("security", JsonSerializer.Serialize(settings, JsonOpts));

    // --------------------
    // EXTERNAL (TMDB / TVMAZE / FANART / IGDB)
    // Stockage: 3 clés séparées dans app_settings:
    // - tmdb_api_key
    // - tvmaze_api_key
    // - fanart_api_key
    // - igdb_client_id
    // - igdb_client_secret
    // - tvmaze_enabled
    // + fallback legacy: JSON sous la clé "external"
    // --------------------

    // ⚠️ Si tu n’as pas encore ce model, il doit exister:
    // public sealed class ExternalSettings { public string? TmdbApiKey {get;set;} public string? IgdbClientId {get;set;} public string? IgdbClientSecret {get;set;} }

    public ExternalSettings GetExternal(ExternalSettings defaults)
    {
        defaults.TvmazeEnabled ??= true;

        // 1) Nouveau format (clés séparées)
        var tmdbRaw = GetRaw("tmdb_api_key");
        var tvmazeRaw = GetRaw("tvmaze_api_key");
        var fanartRaw = GetRaw("fanart_api_key");
        var igdbIdRaw = GetRaw("igdb_client_id");
        var igdbSecretRaw = GetRaw("igdb_client_secret");

        var tmdbEnabled = GetStringSetting("tmdb_enabled");
        var tvmazeEnabled = GetStringSetting("tvmaze_enabled");
        var fanartEnabled = GetStringSetting("fanart_enabled");
        var igdbEnabled = GetStringSetting("igdb_enabled");

        // si au moins une clé existe dans ce format => on l’utilise
        if (tmdbRaw is not null || tvmazeRaw is not null || fanartRaw is not null || igdbIdRaw is not null || igdbSecretRaw is not null || tmdbEnabled is not null || tvmazeEnabled is not null || fanartEnabled is not null || igdbEnabled is not null)
        {
            defaults.TmdbApiKey = GetExternalApiKeySetting("tmdb_api_key") ?? defaults.TmdbApiKey;
            defaults.TvmazeApiKey = GetExternalApiKeySetting("tvmaze_api_key") ?? defaults.TvmazeApiKey;
            defaults.FanartApiKey = GetExternalApiKeySetting("fanart_api_key") ?? defaults.FanartApiKey;
            defaults.IgdbClientId = GetExternalApiKeySetting("igdb_client_id") ?? defaults.IgdbClientId;
            defaults.IgdbClientSecret = GetExternalApiKeySetting("igdb_client_secret") ?? defaults.IgdbClientSecret;
            defaults.TmdbEnabled = ParseBoolSetting(tmdbEnabled) ?? defaults.TmdbEnabled;
            defaults.TvmazeEnabled = ParseBoolSetting(tvmazeEnabled) ?? defaults.TvmazeEnabled;
            defaults.FanartEnabled = ParseBoolSetting(fanartEnabled) ?? defaults.FanartEnabled;
            defaults.IgdbEnabled = ParseBoolSetting(igdbEnabled) ?? defaults.IgdbEnabled;
            return defaults;
        }

        // 2) Fallback legacy (JSON "external")
        var json = GetRaw("external");
        if (string.IsNullOrWhiteSpace(json)) return defaults;

        try
        {
            var loaded = JsonSerializer.Deserialize<ExternalSettings>(json, JsonOpts);
            if (loaded is null) return defaults;

            defaults.TmdbApiKey = UnprotectExternalApiKey(loaded.TmdbApiKey) ?? defaults.TmdbApiKey;
            defaults.TvmazeApiKey = UnprotectExternalApiKey(loaded.TvmazeApiKey) ?? defaults.TvmazeApiKey;
            defaults.FanartApiKey = UnprotectExternalApiKey(loaded.FanartApiKey) ?? defaults.FanartApiKey;
            defaults.IgdbClientId = UnprotectExternalApiKey(loaded.IgdbClientId) ?? defaults.IgdbClientId;
            defaults.IgdbClientSecret = UnprotectExternalApiKey(loaded.IgdbClientSecret) ?? defaults.IgdbClientSecret;
            defaults.TmdbEnabled = loaded.TmdbEnabled ?? defaults.TmdbEnabled;
            defaults.TvmazeEnabled = loaded.TvmazeEnabled ?? defaults.TvmazeEnabled;
            defaults.FanartEnabled = loaded.FanartEnabled ?? defaults.FanartEnabled;
            defaults.IgdbEnabled = loaded.IgdbEnabled ?? defaults.IgdbEnabled;

            TryMigrateLegacyExternalJson(defaults);
            return defaults;
        }
        catch
        {
            return defaults;
        }
    }

    /// <summary>
    /// Update partiel: si champ null/whitespace => on ne touche pas (style Sonarr)
    /// Stocke dans le nouveau format (clés séparées).
    /// </summary>
    public ExternalSettings SaveExternalPartial(ExternalSettings dto)
    {
        var current = GetExternal(new ExternalSettings());

        if (dto.TmdbApiKey is not null)
            current.TmdbApiKey = dto.TmdbApiKey.Trim();

        if (dto.TvmazeApiKey is not null)
            current.TvmazeApiKey = dto.TvmazeApiKey.Trim();

        if (dto.FanartApiKey is not null)
            current.FanartApiKey = dto.FanartApiKey.Trim();

        if (dto.IgdbClientId is not null)
            current.IgdbClientId = dto.IgdbClientId.Trim();

        if (dto.IgdbClientSecret is not null)
            current.IgdbClientSecret = dto.IgdbClientSecret.Trim();

        if (dto.TmdbEnabled is not null)
            current.TmdbEnabled = dto.TmdbEnabled;
        if (dto.TvmazeEnabled is not null)
            current.TvmazeEnabled = dto.TvmazeEnabled;
        if (dto.FanartEnabled is not null)
            current.FanartEnabled = dto.FanartEnabled;
        if (dto.IgdbEnabled is not null)
            current.IgdbEnabled = dto.IgdbEnabled;

        // ✅ stocke en clés séparées (value_json = JSON string), chiffrées
        UpsertExternalApiKeySetting("tmdb_api_key", current.TmdbApiKey);
        UpsertExternalApiKeySetting("tvmaze_api_key", current.TvmazeApiKey);
        UpsertExternalApiKeySetting("fanart_api_key", current.FanartApiKey);
        UpsertExternalApiKeySetting("igdb_client_id", current.IgdbClientId);
        UpsertExternalApiKeySetting("igdb_client_secret", current.IgdbClientSecret);
        UpsertStringSetting("tmdb_enabled", current.TmdbEnabled == false ? "0" : "1");
        UpsertStringSetting("tvmaze_enabled", current.TvmazeEnabled == false ? "0" : "1");
        UpsertStringSetting("fanart_enabled", current.FanartEnabled == false ? "0" : "1");
        UpsertStringSetting("igdb_enabled", current.IgdbEnabled == false ? "0" : "1");

        return current;
    }

    // Pour ton endpoint /api/settings/external (flags)
    public (bool hasTmdbApiKey, bool hasFanartApiKey, bool hasIgdbClientId, bool hasIgdbClientSecret) GetExternalFlags()
    {
        var ex = GetExternal(new ExternalSettings());
        return (
            !string.IsNullOrWhiteSpace(ex.TmdbApiKey),
            !string.IsNullOrWhiteSpace(ex.FanartApiKey),
            !string.IsNullOrWhiteSpace(ex.IgdbClientId),
            !string.IsNullOrWhiteSpace(ex.IgdbClientSecret)
        );
    }

    // --------------------
    // Helpers DB
    // --------------------
    private string? GetRaw(string key)
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<string?>(
            "SELECT value_json FROM app_settings WHERE key = @k",
            new { k = key }
        );
    }

    private void Upsert(string key, string valueJson)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        conn.Execute(
            """
            INSERT INTO app_settings(key, value_json, updated_at_ts)
            VALUES (@k, @v, @ts)
            ON CONFLICT(key) DO UPDATE SET
              value_json = excluded.value_json,
              updated_at_ts = excluded.updated_at_ts;
            """,
            new { k = key, v = valueJson, ts = now }
        );
    }

    private string? GetStringSetting(string key)
    {
        var raw = GetRaw(key);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            // value_json contient un JSON string (ex: "9473...")
            return JsonSerializer.Deserialize<string>(raw, JsonOpts);
        }
        catch
        {
            // fallback brut
            return raw.Trim().Trim('"');
        }
    }

    private void UpsertStringSetting(string key, string value)
    {
        // stocke en JSON string
        var json = JsonSerializer.Serialize(value ?? "", JsonOpts);
        Upsert(key, json);
    }

    private string? GetExternalApiKeySetting(string key)
    {
        var value = GetStringSetting(key);
        if (value is null)
            return null;

        if (string.IsNullOrWhiteSpace(value))
            return value;

        if (_keyProtection.IsProtected(value))
            return _keyProtection.Unprotect(value);

        try
        {
            var encrypted = _keyProtection.Protect(value);
            UpsertStringSetting(key, encrypted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to migrate plaintext external API key for setting {SettingKey}", key);
        }

        return value;
    }

    private void UpsertExternalApiKeySetting(string key, string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            UpsertStringSetting(key, "");
            return;
        }

        var encrypted = _keyProtection.Protect(trimmed);
        UpsertStringSetting(key, encrypted);
    }

    private string? UnprotectExternalApiKey(string? value)
    {
        if (value is null)
            return null;

        if (string.IsNullOrWhiteSpace(value))
            return value;

        return _keyProtection.Unprotect(value);
    }

    private void TryMigrateLegacyExternalJson(ExternalSettings current)
    {
        try
        {
            foreach (var key in ExternalApiKeySettingKeys)
            {
                if (GetRaw(key) is not null)
                    return;
            }

            UpsertExternalApiKeySetting("tmdb_api_key", current.TmdbApiKey);
            UpsertExternalApiKeySetting("tvmaze_api_key", current.TvmazeApiKey);
            UpsertExternalApiKeySetting("fanart_api_key", current.FanartApiKey);
            UpsertExternalApiKeySetting("igdb_client_id", current.IgdbClientId);
            UpsertExternalApiKeySetting("igdb_client_secret", current.IgdbClientSecret);
            UpsertStringSetting("tmdb_enabled", current.TmdbEnabled == false ? "0" : "1");
            UpsertStringSetting("tvmaze_enabled", current.TvmazeEnabled == false ? "0" : "1");
            UpsertStringSetting("fanart_enabled", current.FanartEnabled == false ? "0" : "1");
            UpsertStringSetting("igdb_enabled", current.IgdbEnabled == false ? "0" : "1");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to migrate legacy external settings");
        }
    }

    private static bool? ParseBoolSetting(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return raw.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };
    }

    private sealed class PassthroughApiKeyProtectionService : IApiKeyProtectionService
    {
        public string Protect(string plainText) => plainText;
        public string Unprotect(string protectedText) => protectedText;
        public bool TryUnprotect(string protectedText, out string plainText)
        {
            plainText = protectedText;
            return true;
        }
        public bool IsProtected(string value) => false;
    }
}
