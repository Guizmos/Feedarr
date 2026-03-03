using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Sync;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Services;

public sealed class RssSyncHostedService : BackgroundService
{
    private readonly ILogger<RssSyncHostedService> _log;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AppOptions _opts;
    private readonly BackupExecutionCoordinator _backupCoordinator;

    public RssSyncHostedService(
        ILogger<RssSyncHostedService> log,
        IServiceScopeFactory scopeFactory,
        IOptions<AppOptions> opts,
        BackupExecutionCoordinator backupCoordinator)
    {
        _log = log;
        _scopeFactory = scopeFactory;
        _opts = opts.Value;
        _backupCoordinator = backupCoordinator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;

            try
            {
                if (IsAutoSyncEnabled())
                {
                    using var lease = _backupCoordinator.TryEnterSyncActivity("rss-auto-sync");
                    if (lease is null)
                    {
                        _log.LogInformation("AutoSync skipped: backup operation in progress");
                    }
                    else
                    {
                        var hadFailure = await RunOnce(stoppingToken).ConfigureAwait(false);
                        using var scope = _scopeFactory.CreateScope();
                        var providerStats = scope.ServiceProvider.GetRequiredService<ProviderStatsService>();
                        providerStats.RecordSyncJob(!hadFailure);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var providerStats = scope.ServiceProvider.GetRequiredService<ProviderStatsService>();
                    providerStats.RecordSyncJob(false);
                }
                catch (Exception statsEx)
                {
                    _log.LogWarning(statsEx, "Failed to record sync-job failure stats");
                }

                _log.LogError(ex, "RssSyncHostedService loop error");
            }

            var intervalMin = GetIntervalMinutesFromDbOrOpts(stoppingToken);
            if (!IsAutoSyncEnabled())
                intervalMin = Math.Max(intervalMin, 60);

            var elapsed = DateTimeOffset.UtcNow - started;
            var sleep = TimeSpan.FromMinutes(intervalMin) - elapsed;
            if (sleep < TimeSpan.FromSeconds(1))
                sleep = TimeSpan.FromSeconds(1);

            await Task.Delay(sleep, stoppingToken).ConfigureAwait(false);
        }
    }

    private int GetIntervalMinutesFromDbOrOpts(CancellationToken ct)
    {
        var fallback = Math.Clamp(_opts.SyncIntervalMinutes, 1, 1440);

        try
        {
            ct.ThrowIfCancellationRequested();

            using var scope = _scopeFactory.CreateScope();
            var settingsRepo = scope.ServiceProvider.GetRequiredService<SettingsRepository>();
            var general = settingsRepo.GetGeneral(new GeneralSettings
            {
                SyncIntervalMinutes = fallback,
                RssLimit = Math.Clamp(_opts.RssLimit <= 0 ? 100 : _opts.RssLimit, 1, 200)
            });

            return Math.Clamp(general.SyncIntervalMinutes, 1, 1440);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read sync interval from DB, using fallback {Fallback}min", fallback);
            return fallback;
        }
    }

    private bool IsAutoSyncEnabled()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var settingsRepo = scope.ServiceProvider.GetRequiredService<SettingsRepository>();
            var general = settingsRepo.GetGeneral(new GeneralSettings
            {
                SyncIntervalMinutes = Math.Clamp(_opts.SyncIntervalMinutes, 1, 1440),
                RssLimit = Math.Clamp(_opts.RssLimit <= 0 ? 100 : _opts.RssLimit, 1, 200),
                AutoSyncEnabled = true
            });
            return general.AutoSyncEnabled;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read auto-sync setting from DB, defaulting to enabled");
            return true;
        }
    }

    private async Task<bool> RunOnce(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var sources = scope.ServiceProvider.GetRequiredService<SourceRepository>();
        var sync = scope.ServiceProvider.GetRequiredService<SyncOrchestrationService>();

        var list = sources.ListEnabledForSync();
        if (list.Count == 0)
        {
            _log.LogInformation("AutoSync: no sources");
            return false;
        }

        return await sync.ExecuteSourcesAsync(list, new AutoSyncPolicy(), _opts.RssOnlySync, ct).ConfigureAwait(false);
    }
}
