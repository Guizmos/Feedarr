using Dapper;
using Feedarr.Api.Models.Arr;
using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Data.Repositories;

public sealed class ArrApplicationRepository
{
    private readonly Db _db;
    private readonly IApiKeyProtectionService _keyProtection;

    public ArrApplicationRepository(Db db, IApiKeyProtectionService keyProtection)
    {
        _db = db;
        _keyProtection = keyProtection;
    }

    public long Create(
        string type,
        string? name,
        string baseUrl,
        string apiKeyEncrypted,
        string? rootFolderPath,
        int? qualityProfileId,
        string? tags,
        string? seriesType,
        bool seasonFolder,
        string? monitorMode,
        bool searchMissing,
        bool searchCutoff,
        string? minimumAvailability,
        bool searchForMovie)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var encryptedKey = _keyProtection.Protect(apiKeyEncrypted);

        return conn.ExecuteScalar<long>(
            """
            INSERT INTO arr_applications(
                type, name, base_url, api_key_encrypted, is_enabled, is_default,
                root_folder_path, quality_profile_id, tags,
                series_type, season_folder, monitor_mode, search_missing, search_cutoff,
                minimum_availability, search_for_movie,
                created_at_ts, updated_at_ts
            )
            VALUES (
                @type, @name, @baseUrl, @apiKey, 1, 0,
                @rootFolderPath, @qualityProfileId, @tags,
                @seriesType, @seasonFolder, @monitorMode, @searchMissing, @searchCutoff,
                @minimumAvailability, @searchForMovie,
                @now, @now
            );
            SELECT last_insert_rowid();
            """,
            new
            {
                type,
                name,
                baseUrl,
                apiKey = encryptedKey,
                rootFolderPath,
                qualityProfileId,
                tags,
                seriesType,
                seasonFolder = seasonFolder ? 1 : 0,
                monitorMode,
                searchMissing = searchMissing ? 1 : 0,
                searchCutoff = searchCutoff ? 1 : 0,
                minimumAvailability,
                searchForMovie = searchForMovie ? 1 : 0,
                now
            }
        );
    }

    public IEnumerable<ArrApplication> List()
    {
        using var conn = _db.Open();
        var apps = conn.Query<ArrApplication>(
            """
            SELECT
                id as Id,
                type as Type,
                name as Name,
                base_url as BaseUrl,
                api_key_encrypted as ApiKeyEncrypted,
                is_enabled as IsEnabled,
                is_default as IsDefault,
                root_folder_path as RootFolderPath,
                quality_profile_id as QualityProfileId,
                tags as Tags,
                series_type as SeriesType,
                season_folder as SeasonFolder,
                monitor_mode as MonitorMode,
                search_missing as SearchMissing,
                search_cutoff as SearchCutoff,
                minimum_availability as MinimumAvailability,
                search_for_movie as SearchForMovie,
                created_at_ts as CreatedAtTs,
                updated_at_ts as UpdatedAtTs
            FROM arr_applications
            ORDER BY type, id;
            """
        ).AsList();

        foreach (var app in apps)
        {
            if (!string.IsNullOrWhiteSpace(app.ApiKeyEncrypted))
                app.ApiKeyEncrypted = _keyProtection.Unprotect(app.ApiKeyEncrypted);
        }

        return apps;
    }

    public IEnumerable<ArrApplication> ListByType(string type)
    {
        using var conn = _db.Open();
        var apps = conn.Query<ArrApplication>(
            """
            SELECT
                id as Id,
                type as Type,
                name as Name,
                base_url as BaseUrl,
                api_key_encrypted as ApiKeyEncrypted,
                is_enabled as IsEnabled,
                is_default as IsDefault,
                root_folder_path as RootFolderPath,
                quality_profile_id as QualityProfileId,
                tags as Tags,
                series_type as SeriesType,
                season_folder as SeasonFolder,
                monitor_mode as MonitorMode,
                search_missing as SearchMissing,
                search_cutoff as SearchCutoff,
                minimum_availability as MinimumAvailability,
                search_for_movie as SearchForMovie,
                created_at_ts as CreatedAtTs,
                updated_at_ts as UpdatedAtTs
            FROM arr_applications
            WHERE type = @type
            ORDER BY id;
            """,
            new { type }
        ).AsList();

        foreach (var app in apps)
        {
            if (!string.IsNullOrWhiteSpace(app.ApiKeyEncrypted))
                app.ApiKeyEncrypted = _keyProtection.Unprotect(app.ApiKeyEncrypted);
        }

        return apps;
    }

    public ArrApplication? Get(long id)
    {
        using var conn = _db.Open();
        var app = conn.QueryFirstOrDefault<ArrApplication>(
            """
            SELECT
                id as Id,
                type as Type,
                name as Name,
                base_url as BaseUrl,
                api_key_encrypted as ApiKeyEncrypted,
                is_enabled as IsEnabled,
                is_default as IsDefault,
                root_folder_path as RootFolderPath,
                quality_profile_id as QualityProfileId,
                tags as Tags,
                series_type as SeriesType,
                season_folder as SeasonFolder,
                monitor_mode as MonitorMode,
                search_missing as SearchMissing,
                search_cutoff as SearchCutoff,
                minimum_availability as MinimumAvailability,
                search_for_movie as SearchForMovie,
                created_at_ts as CreatedAtTs,
                updated_at_ts as UpdatedAtTs
            FROM arr_applications
            WHERE id = @id;
            """,
            new { id }
        );

        if (app is not null && !string.IsNullOrWhiteSpace(app.ApiKeyEncrypted))
            app.ApiKeyEncrypted = _keyProtection.Unprotect(app.ApiKeyEncrypted);

        return app;
    }

    public ArrApplication? GetDefault(string type)
    {
        using var conn = _db.Open();
        var app = conn.QueryFirstOrDefault<ArrApplication>(
            """
            SELECT
                id as Id,
                type as Type,
                name as Name,
                base_url as BaseUrl,
                api_key_encrypted as ApiKeyEncrypted,
                is_enabled as IsEnabled,
                is_default as IsDefault,
                root_folder_path as RootFolderPath,
                quality_profile_id as QualityProfileId,
                tags as Tags,
                series_type as SeriesType,
                season_folder as SeasonFolder,
                monitor_mode as MonitorMode,
                search_missing as SearchMissing,
                search_cutoff as SearchCutoff,
                minimum_availability as MinimumAvailability,
                search_for_movie as SearchForMovie,
                created_at_ts as CreatedAtTs,
                updated_at_ts as UpdatedAtTs
            FROM arr_applications
            WHERE type = @type AND is_enabled = 1
            ORDER BY is_default DESC, id ASC
            LIMIT 1;
            """,
            new { type }
        );

        if (app is not null && !string.IsNullOrWhiteSpace(app.ApiKeyEncrypted))
            app.ApiKeyEncrypted = _keyProtection.Unprotect(app.ApiKeyEncrypted);

        return app;
    }

    public bool Update(
        long id,
        string? name,
        string? baseUrl,
        string? apiKeyEncryptedOrNull,
        string? rootFolderPath,
        int? qualityProfileId,
        string? tags,
        string? seriesType,
        bool? seasonFolder,
        string? monitorMode,
        bool? searchMissing,
        bool? searchCutoff,
        string? minimumAvailability,
        bool? searchForMovie)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var current = Get(id);
        if (current is null) return false;

        var finalName = name ?? current.Name;
        var finalBaseUrl = baseUrl ?? current.BaseUrl;
        var finalApiKey = apiKeyEncryptedOrNull ?? current.ApiKeyEncrypted;
        var finalApiKeyEncrypted = _keyProtection.Protect(finalApiKey);
        var finalRootFolder = rootFolderPath ?? current.RootFolderPath;
        var finalQualityProfile = qualityProfileId ?? current.QualityProfileId;
        var finalTags = tags ?? current.Tags;
        var finalSeriesType = seriesType ?? current.SeriesType;
        var finalSeasonFolder = seasonFolder ?? current.SeasonFolder;
        var finalMonitorMode = monitorMode ?? current.MonitorMode;
        var finalSearchMissing = searchMissing ?? current.SearchMissing;
        var finalSearchCutoff = searchCutoff ?? current.SearchCutoff;
        var finalMinAvail = minimumAvailability ?? current.MinimumAvailability;
        var finalSearchMovie = searchForMovie ?? current.SearchForMovie;

        var rows = conn.Execute(
            """
            UPDATE arr_applications
            SET name = @name,
                base_url = @baseUrl,
                api_key_encrypted = @apiKey,
                root_folder_path = @rootFolderPath,
                quality_profile_id = @qualityProfileId,
                tags = @tags,
                series_type = @seriesType,
                season_folder = @seasonFolder,
                monitor_mode = @monitorMode,
                search_missing = @searchMissing,
                search_cutoff = @searchCutoff,
                minimum_availability = @minimumAvailability,
                search_for_movie = @searchForMovie,
                updated_at_ts = @now
            WHERE id = @id;
            """,
            new
            {
                id,
                name = finalName,
                baseUrl = finalBaseUrl,
                apiKey = finalApiKeyEncrypted,
                rootFolderPath = finalRootFolder,
                qualityProfileId = finalQualityProfile,
                tags = finalTags,
                seriesType = finalSeriesType,
                seasonFolder = finalSeasonFolder ? 1 : 0,
                monitorMode = finalMonitorMode,
                searchMissing = finalSearchMissing ? 1 : 0,
                searchCutoff = finalSearchCutoff ? 1 : 0,
                minimumAvailability = finalMinAvail,
                searchForMovie = finalSearchMovie ? 1 : 0,
                now
            }
        );

        return rows > 0;
    }

    public bool SetEnabled(long id, bool enabled)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var rows = conn.Execute(
            """
            UPDATE arr_applications
            SET is_enabled = @en, updated_at_ts = @now
            WHERE id = @id;
            """,
            new { id, en = enabled ? 1 : 0, now }
        );

        return rows > 0;
    }

    public bool SetDefault(long id)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var app = Get(id);
        if (app is null) return false;

        using var tx = conn.BeginTransaction();

        // Remove default from all apps of same type
        conn.Execute(
            "UPDATE arr_applications SET is_default = 0, updated_at_ts = @now WHERE type = @type",
            new { type = app.Type, now },
            tx
        );

        // Set this one as default
        conn.Execute(
            "UPDATE arr_applications SET is_default = 1, updated_at_ts = @now WHERE id = @id",
            new { id, now },
            tx
        );

        tx.Commit();
        return true;
    }

    public int Delete(long id)
    {
        using var conn = _db.Open();
        return conn.Execute("DELETE FROM arr_applications WHERE id = @id", new { id });
    }
}
