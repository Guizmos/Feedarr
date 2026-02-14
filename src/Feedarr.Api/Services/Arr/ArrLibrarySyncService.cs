using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Services.Arr;

/// <summary>
/// Background service that periodically syncs apps to the database.
/// - Sonarr/Radarr: library items
/// - Overseerr/Jellyseerr: requests count
/// Sync interval and auto-sync can be configured in Settings > General.
/// </summary>
public sealed class ArrLibrarySyncService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ArrLibrarySyncService> _logger;
    private readonly BackupExecutionCoordinator _backupCoordinator;
    private const int DefaultSyncIntervalMinutes = 60;

    private static bool IsLibrarySyncType(string? appType)
        => string.Equals(appType, "sonarr", StringComparison.OrdinalIgnoreCase)
            || string.Equals(appType, "radarr", StringComparison.OrdinalIgnoreCase);

    private static bool IsRequestSyncType(string? appType)
        => string.Equals(appType, "overseerr", StringComparison.OrdinalIgnoreCase)
            || string.Equals(appType, "jellyseerr", StringComparison.OrdinalIgnoreCase);

    private static bool IsSyncType(string? appType)
        => IsLibrarySyncType(appType) || IsRequestSyncType(appType);

    public ArrLibrarySyncService(
        IServiceScopeFactory scopeFactory,
        ILogger<ArrLibrarySyncService> logger,
        BackupExecutionCoordinator backupCoordinator)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _backupCoordinator = backupCoordinator;
    }

    private GeneralSettings GetSettings()
    {
        using var scope = _scopeFactory.CreateScope();
        var settingsRepo = scope.ServiceProvider.GetRequiredService<SettingsRepository>();
        var defaults = new GeneralSettings
        {
            ArrSyncIntervalMinutes = DefaultSyncIntervalMinutes,
            ArrAutoSyncEnabled = true
        };
        return settingsRepo.GetGeneral(defaults);
    }

    private bool HasEnabledArrApps()
    {
        using var scope = _scopeFactory.CreateScope();
        var appRepo = scope.ServiceProvider.GetRequiredService<ArrApplicationRepository>();
        return appRepo.List().Any(a =>
            IsSyncType(a.Type) &&
            a.IsEnabled &&
            !string.IsNullOrWhiteSpace(a.ApiKeyEncrypted));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ArrLibrarySyncService started");

        // Wait a bit before first sync to let the app start up
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        // Initial sync on startup (only if at least one app is enabled)
        if (HasEnabledArrApps())
        {
            await SyncAllAppsAsync(stoppingToken, skipWhenBackupBusy: true);
        }

        // Periodic sync with configurable interval
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = GetSettings();
                var syncInterval = TimeSpan.FromMinutes(Math.Max(1, settings.ArrSyncIntervalMinutes));

                if (!settings.ArrAutoSyncEnabled)
                {
                    _logger.LogDebug("Arr auto-sync is disabled, waiting 1 minute before checking again");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                if (!HasEnabledArrApps())
                {
                    _logger.LogDebug("Arr auto-sync skipped (no enabled apps), waiting 1 minute before checking again");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                _logger.LogDebug("Next Arr sync in {Interval} minutes", syncInterval.TotalMinutes);
                await Task.Delay(syncInterval, stoppingToken);

                // Re-check settings in case they changed while waiting
                settings = GetSettings();
                if (settings.ArrAutoSyncEnabled && HasEnabledArrApps())
                {
                    await SyncAllAppsAsync(stoppingToken, skipWhenBackupBusy: true);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in periodic sync loop");
                // Wait a bit before retrying to avoid tight loop on persistent errors
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }

        _logger.LogInformation("ArrLibrarySyncService stopped");
    }

    /// <summary>
    /// Sync all enabled apps
    /// </summary>
    public async Task SyncAllAppsAsync(CancellationToken ct, bool skipWhenBackupBusy = false)
    {
        using var syncLease = _backupCoordinator.TryEnterSyncActivity("arr-sync-all");
        if (syncLease is null)
        {
            if (!skipWhenBackupBusy)
            {
                throw new BackupOperationException(
                    "backup operation in progress",
                    Microsoft.AspNetCore.Http.StatusCodes.Status409Conflict);
            }

            _logger.LogInformation("Arr sync skipped: backup operation in progress");
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var appRepo = scope.ServiceProvider.GetRequiredService<ArrApplicationRepository>();
        var libraryRepo = scope.ServiceProvider.GetRequiredService<ArrLibraryRepository>();
        var sonarr = scope.ServiceProvider.GetRequiredService<SonarrClient>();
        var radarr = scope.ServiceProvider.GetRequiredService<RadarrClient>();
        var eerr = scope.ServiceProvider.GetRequiredService<EerrRequestClient>();

        var apps = appRepo.List();
        var enabledApps = apps.Where(a =>
                IsSyncType(a.Type) &&
                a.IsEnabled &&
                !string.IsNullOrWhiteSpace(a.ApiKeyEncrypted))
            .ToList();

        if (enabledApps.Count == 0)
        {
            _logger.LogDebug("No enabled Arr apps with API keys configured");
            return;
        }

        _logger.LogInformation("Starting library sync for {Count} enabled app(s)", enabledApps.Count);

        foreach (var app in enabledApps)
        {
            try
            {
                if (app.Type == "sonarr")
                {
                    await SyncSonarrLibraryAsync(app, sonarr, libraryRepo, ct);
                }
                else if (app.Type == "radarr")
                {
                    await SyncRadarrLibraryAsync(app, radarr, libraryRepo, ct);
                }
                else if (app.Type == "overseerr" || app.Type == "jellyseerr")
                {
                    await SyncRequestAppAsync(app, eerr, libraryRepo, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync {Type} app '{Name}' (id={Id})", app.Type, app.Name, app.Id);
                var safeError = ErrorMessageSanitizer.ToOperationalMessage(ex, "arr library sync failed");
                libraryRepo.SetSyncError(app.Id, safeError);
            }
        }

        _logger.LogInformation("Library sync completed");
    }

    /// <summary>
    /// Sync a specific app by ID
    /// </summary>
    public async Task SyncAppAsync(long appId, CancellationToken ct, bool skipWhenBackupBusy = false)
    {
        using var syncLease = _backupCoordinator.TryEnterSyncActivity("arr-sync-app");
        if (syncLease is null)
        {
            if (!skipWhenBackupBusy)
            {
                throw new BackupOperationException(
                    "backup operation in progress",
                    Microsoft.AspNetCore.Http.StatusCodes.Status409Conflict);
            }

            _logger.LogInformation("Arr app sync skipped for app {AppId}: backup operation in progress", appId);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var appRepo = scope.ServiceProvider.GetRequiredService<ArrApplicationRepository>();
        var libraryRepo = scope.ServiceProvider.GetRequiredService<ArrLibraryRepository>();
        var sonarr = scope.ServiceProvider.GetRequiredService<SonarrClient>();
        var radarr = scope.ServiceProvider.GetRequiredService<RadarrClient>();
        var eerr = scope.ServiceProvider.GetRequiredService<EerrRequestClient>();

        var app = appRepo.Get(appId);
        if (app is null)
        {
            _logger.LogWarning("App {Id} not found for sync", appId);
            return;
        }

        if (!app.IsEnabled || string.IsNullOrWhiteSpace(app.ApiKeyEncrypted))
        {
            _logger.LogDebug("App {Id} is disabled or has no API key, skipping sync", appId);
            return;
        }

        try
        {
            if (app.Type == "sonarr")
            {
                await SyncSonarrLibraryAsync(app, sonarr, libraryRepo, ct);
            }
            else if (app.Type == "radarr")
            {
                await SyncRadarrLibraryAsync(app, radarr, libraryRepo, ct);
            }
            else if (app.Type == "overseerr" || app.Type == "jellyseerr")
            {
                await SyncRequestAppAsync(app, eerr, libraryRepo, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync {Type} app '{Name}' (id={Id})", app.Type, app.Name, app.Id);
            var safeError = ErrorMessageSanitizer.ToOperationalMessage(ex, "arr library sync failed");
            libraryRepo.SetSyncError(app.Id, safeError);
            throw;
        }
    }

    private async Task SyncSonarrLibraryAsync(
        Models.Arr.ArrApplication app,
        SonarrClient sonarr,
        ArrLibraryRepository libraryRepo,
        CancellationToken ct)
    {
        _logger.LogInformation("Syncing Sonarr library for '{Name}' ({Url})", app.Name, app.BaseUrl);

        var series = await sonarr.GetAllSeriesAsync(app.BaseUrl, app.ApiKeyEncrypted, ct);
        _logger.LogInformation("Fetched {Total} series from Sonarr API", series.Count);

        // Log series without tvdbId for debugging
        var seriesWithoutTvdbId = series.Where(s => s.TvdbId <= 0).ToList();
        if (seriesWithoutTvdbId.Count > 0)
        {
            _logger.LogWarning("Found {Count} series without tvdbId (title match only)", seriesWithoutTvdbId.Count);
        }

        var items = series
            .Select(s => new LibraryItemDto
            {
                TvdbId = s.TvdbId > 0 ? s.TvdbId : null,
                InternalId = s.Id,
                Title = s.Title,
                TitleSlug = s.TitleSlug,
                AlternateTitles = s.AlternateTitles?.Select(a => a.Title).Where(t => !string.IsNullOrWhiteSpace(t)).ToList()
            })
            .ToList();

        // Log sample series for debugging
        if (items.Count > 0)
        {
            var sample = items.Take(10).Select(i => $"{i.Title} (tvdbId={i.TvdbId}, slug={i.TitleSlug})");
            _logger.LogInformation("Sample series from Sonarr: {Sample}", string.Join("; ", sample));
        }
        else
        {
            _logger.LogWarning("No valid series to sync from Sonarr '{Name}'", app.Name);
        }

        libraryRepo.SyncAppLibrary(app.Id, "series", items);
        _logger.LogInformation("Synced {Count} series from Sonarr '{Name}' to database (appId={AppId})", items.Count, app.Name, app.Id);
    }

    private async Task SyncRadarrLibraryAsync(
        Models.Arr.ArrApplication app,
        RadarrClient radarr,
        ArrLibraryRepository libraryRepo,
        CancellationToken ct)
    {
        _logger.LogInformation("Syncing Radarr library for '{Name}' ({Url})", app.Name, app.BaseUrl);

        var movies = await radarr.GetAllMoviesAsync(app.BaseUrl, app.ApiKeyEncrypted, ct);
        _logger.LogInformation("Fetched {Total} movies from Radarr API", movies.Count);

        // Log movies without tmdbId for debugging
        var moviesWithoutTmdbId = movies.Where(m => m.TmdbId <= 0).ToList();
        if (moviesWithoutTmdbId.Count > 0)
        {
            _logger.LogWarning("Found {Count} movies without tmdbId (title match only)", moviesWithoutTmdbId.Count);
        }

        var items = movies
            .Select(m =>
            {
                var altTitles = new List<string>();
                if (!string.IsNullOrWhiteSpace(m.OriginalTitle) && m.OriginalTitle != m.Title)
                    altTitles.Add(m.OriginalTitle);
                if (m.AlternateTitles?.Count > 0)
                    altTitles.AddRange(m.AlternateTitles.Select(a => a.Title).Where(t => !string.IsNullOrWhiteSpace(t)));

                return new LibraryItemDto
                {
                    TmdbId = m.TmdbId > 0 ? m.TmdbId : null,
                    InternalId = m.Id,
                    Title = m.Title,
                    OriginalTitle = m.OriginalTitle,
                    AlternateTitles = altTitles.Count > 0 ? altTitles.Distinct().ToList() : null
                };
            })
            .ToList();

        // Log sample movies for debugging
        if (items.Count > 0)
        {
            var sample = items.Take(10).Select(i => $"{i.Title} (tmdbId={i.TmdbId})");
            _logger.LogInformation("Sample movies from Radarr: {Sample}", string.Join("; ", sample));
        }
        else
        {
            _logger.LogWarning("No valid movies to sync from Radarr '{Name}'", app.Name);
        }

        libraryRepo.SyncAppLibrary(app.Id, "movie", items);
        _logger.LogInformation("Synced {Count} movies from Radarr '{Name}' to database (appId={AppId})", items.Count, app.Name, app.Id);
    }

    private async Task SyncRequestAppAsync(
        Models.Arr.ArrApplication app,
        EerrRequestClient eerr,
        ArrLibraryRepository libraryRepo,
        CancellationToken ct)
    {
        _logger.LogInformation("Syncing {Type} requests for '{Name}' ({Url})", app.Type, app.Name, app.BaseUrl);

        var requests = await eerr.GetRequestsAsync(app.BaseUrl, app.ApiKeyEncrypted, ct);
        libraryRepo.SetSyncSuccess(app.Id, requests.Count);

        _logger.LogInformation(
            "Synced {Count} requests from {Type} '{Name}' (appId={AppId})",
            requests.Count,
            app.Type,
            app.Name,
            app.Id);
    }
}
