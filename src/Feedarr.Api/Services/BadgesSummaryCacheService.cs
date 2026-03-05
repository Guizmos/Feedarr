using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Backup;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Services;

public sealed record BadgesBaseSummary(
    long LastActivityTs,
    int SourcesCount,
    int ReleasesCount,
    long ReleasesLatestTs,
    bool IncludeInfo,
    bool IncludeWarn,
    bool IncludeError,
    int MissingExternalCount,
    bool HasAdvancedMaintenanceEnabled,
    bool IsSyncRunning,
    bool SchedulerBusy,
    bool UpdatesBadge);

public interface IBadgesBaseSummaryProvider
{
    Task<BadgesBaseSummary> LoadAsync(CancellationToken ct);
}

public sealed class BadgesBaseSummaryProvider : IBadgesBaseSummaryProvider
{
    private readonly Db _db;
    private readonly SettingsRepository _settings;
    private readonly BackupExecutionCoordinator _backupCoordinator;

    public BadgesBaseSummaryProvider(
        Db db,
        SettingsRepository settings,
        BackupExecutionCoordinator backupCoordinator)
    {
        _db = db;
        _settings = settings;
        _backupCoordinator = backupCoordinator;
    }

    public Task<BadgesBaseSummary> LoadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var settings = _settings.GetBadgesRelevantSettingsSnapshot();

        int sourcesCount;
        int releasesCount;
        long releasesLatestTs;
        long lastActivityTs;

        using (var conn = _db.Open())
        using (var stats = conn.QueryMultiple(
                   """
                   SELECT COUNT(1) FROM sources;
                   SELECT COUNT(1) FROM releases;
                   SELECT COALESCE(MAX(created_at_ts), 0) FROM releases;
                   SELECT COALESCE(MAX(created_at_ts), 0) FROM activity_log;
                   """))
        {
            sourcesCount = stats.ReadSingle<int>();
            releasesCount = stats.ReadSingle<int>();
            releasesLatestTs = stats.ReadSingle<long>();
            lastActivityTs = stats.ReadSingle<long>();
        }

        var backupState = _backupCoordinator.GetState();
        var missingExternalCount = 0;
        if (!settings.HasTmdbApiKey) missingExternalCount++;
        if (!settings.HasIgdbClientId) missingExternalCount++;
        if (!settings.HasIgdbClientSecret) missingExternalCount++;

        var result = new BadgesBaseSummary(
            LastActivityTs: lastActivityTs,
            SourcesCount: sourcesCount,
            ReleasesCount: releasesCount,
            ReleasesLatestTs: releasesLatestTs,
            IncludeInfo: settings.BadgeInfo,
            IncludeWarn: settings.BadgeWarn,
            IncludeError: settings.BadgeError,
            MissingExternalCount: missingExternalCount,
            HasAdvancedMaintenanceEnabled: settings.HasAdvancedMaintenanceEnabled,
            IsSyncRunning: backupState.ActiveSyncActivities > 0,
            SchedulerBusy: backupState.IsBusy || backupState.SyncBlocked,
            UpdatesBadge: false);

        return Task.FromResult(result);
    }
}

public sealed class BadgesSummaryCacheService
{
    private const string CacheKey = "badges:base:v1";
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(3);

    private readonly IMemoryCache _cache;
    private readonly IBadgesBaseSummaryProvider _provider;
    private readonly ILogger<BadgesSummaryCacheService> _log;
    private readonly TimeSpan _ttl;
    private readonly object _inflightLock = new();
    private Task<BadgesBaseSummary>? _inflightLoad;

    public BadgesSummaryCacheService(
        IMemoryCache cache,
        IBadgesBaseSummaryProvider provider,
        IOptions<AppOptions> options,
        ILogger<BadgesSummaryCacheService> log)
    {
        _cache = cache;
        _provider = provider;
        _log = log;

        var configuredSeconds = options?.Value?.BadgesSummaryCacheSeconds ?? 3;
        _ttl = configuredSeconds > 0
            ? TimeSpan.FromSeconds(Math.Clamp(configuredSeconds, 1, 30))
            : DefaultTtl;
    }

    public async Task<BadgesBaseSummary> GetBaseSummaryAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue<BadgesBaseSummary>(CacheKey, out var cached))
        {
            _log.LogDebug("BadgesBaseSummary cache hit");
            return cached!;
        }

        Task<BadgesBaseSummary> loadTask;
        lock (_inflightLock)
        {
            if (_inflightLoad is not null)
            {
                _log.LogDebug("BadgesBaseSummary cache miss with in-flight load");
                loadTask = _inflightLoad;
            }
            else
            {
                _log.LogDebug("BadgesBaseSummary cache miss, loading shared summary");
                _inflightLoad = LoadAndCacheAsync();
                loadTask = _inflightLoad;
            }
        }

        return await loadTask.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<BadgesBaseSummary> LoadAndCacheAsync()
    {
        try
        {
            var loaded = await _provider.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            _cache.Set(CacheKey, loaded, _ttl);
            return loaded;
        }
        finally
        {
            lock (_inflightLock)
            {
                _inflightLoad = null;
            }
        }
    }
}
