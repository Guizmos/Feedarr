using Microsoft.AspNetCore.Mvc;
using Dapper;
using System.Reflection;
using System.Text.RegularExpressions;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Dtos.System;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Diagnostics;
using Feedarr.Api.Services.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.RateLimiting;
using System.Data;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/system")]
public sealed class SystemController : ControllerBase
{
    private sealed class SourceStatsRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public int Enabled { get; set; }
        public long? LastSyncAtTs { get; set; }
        public string LastStatus { get; set; } = "";
        public string? LastError { get; set; }
        public int LastItemCount { get; set; }
        public int ReleaseCount { get; set; }
    }

    private static readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private static readonly TimeSpan StorageUsageCacheDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StatsCacheDuration = TimeSpan.FromSeconds(20);
    private const string StorageUsageCacheKey = "system:storage-usage:v1";
    private static readonly object StorageRefreshLock = new();
    private static Task? StorageRefreshTask;
    private static StorageUsageSnapshot LastKnownStorageUsage = new(0, 0, 0, 0, 0, 0);
    private static bool HasStorageSnapshot;
    private static readonly Regex _semVerRegex = new(
        @"(?<!\d)v?(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
    );

    private readonly Db _db;
    private readonly IWebHostEnvironment _env;
    private readonly AppOptions _opts;
    private readonly SettingsRepository _settings;
    private readonly ProviderStatsService _providerStats;
    private readonly ApiRequestMetricsService _apiRequestMetrics;
    private readonly BackupService _backupService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SystemController> _log;

    private string DataDirAbs =>
        Path.IsPathRooted(_opts.DataDir)
            ? _opts.DataDir
            : Path.GetFullPath(Path.Combine(_env.ContentRootPath, _opts.DataDir));

    private string BackupDirAbs => Path.Combine(DataDirAbs, "backups");

    private string DbPathAbs => Path.Combine(DataDirAbs, _opts.DbFileName);

    public SystemController(
        Db db,
        IWebHostEnvironment env,
        Microsoft.Extensions.Options.IOptions<AppOptions> opts,
        SettingsRepository settings,
        ProviderStatsService providerStats,
        ApiRequestMetricsService apiRequestMetrics,
        BackupService backupService,
        IMemoryCache cache,
        ILogger<SystemController> log)
    {
        _db = db;
        _env = env;
        _opts = opts.Value;
        _settings = settings;
        _providerStats = providerStats;
        _apiRequestMetrics = apiRequestMetrics;
        _backupService = backupService;
        _cache = cache;
        _log = log;
    }

    [HttpGet("status")]
    public IActionResult Status([FromQuery] long? releasesSinceTs = null)
    {
        using var conn = _db.Open();

        var safeSinceTs = Math.Max(0L, releasesSinceTs ?? 0L);

        using var multi = conn.QueryMultiple(
            """
            SELECT COUNT(1) FROM sources;
            SELECT COUNT(1) FROM releases;
            SELECT MAX(created_at_ts) FROM releases;
            SELECT COUNT(1) FROM releases WHERE created_at_ts > @sinceTs;
            SELECT MAX(last_sync_at_ts) FROM sources;
            """,
            new { sinceTs = safeSinceTs });

        var sourcesCount = multi.ReadSingle<int>();
        var releasesCount = multi.ReadSingle<int>();
        var releasesLatestTs = multi.ReadSingleOrDefault<long?>();
        var sinceCount = multi.ReadSingle<int>();
        int? releasesNewSinceTsCount = safeSinceTs > 0 ? sinceCount : null;

        long? lastSyncAt = multi.ReadSingleOrDefault<long?>();

        var version = GetAppVersion();
        var dbSizeMb = 0.0;
        try
        {
            if (System.IO.File.Exists(_db.DbPath))
            {
                var bytes = new FileInfo(_db.DbPath).Length;
                dbSizeMb = Math.Round(bytes / 1024d / 1024d, 1);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read database size");
            dbSizeMb = 0.0;
        }

        var dto = new SystemStatusDto
        {
            Version = version,
            Environment = _env.EnvironmentName,
            UptimeSeconds = (long)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
            DataDir = "hidden",
            DbPath = Path.GetFileName(_db.DbPath),
            DbSizeMB = dbSizeMb,
            SourcesCount = sourcesCount,
            ReleasesCount = releasesCount,
            ReleasesLatestTs = releasesLatestTs,
            ReleasesNewSinceTsCount = releasesNewSinceTsCount,
            LastSyncAtTs = lastSyncAt
        };

        return Ok(dto);
    }

    // GET /api/system/providers
    [HttpGet("providers")]
    public IActionResult Providers()
    {
        var stats = _providerStats.Snapshot();

        return Ok(new
        {
            tmdb = new { calls = stats.Tmdb.Calls, failures = stats.Tmdb.Failures, avgMs = stats.Tmdb.AvgMs },
            tvmaze = new { calls = stats.Tvmaze.Calls, failures = stats.Tvmaze.Failures, avgMs = stats.Tvmaze.AvgMs },
            fanart = new { calls = stats.Fanart.Calls, failures = stats.Fanart.Failures, avgMs = stats.Fanart.AvgMs },
            igdb = new { calls = stats.Igdb.Calls, failures = stats.Igdb.Failures, avgMs = stats.Igdb.AvgMs }
        });
    }

    // GET /api/system/perf
    [HttpGet("perf")]
    public IActionResult Performance([FromQuery] int top = 20)
    {
        return Ok(_apiRequestMetrics.Snapshot(top));
    }

    // GET /api/system/onboarding
    [HttpGet("onboarding")]
    public IActionResult Onboarding()
    {
        using var conn = _db.Open();

        var sourcesCount = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM sources;");

        var ext = _settings.GetExternal(new Models.Settings.ExternalSettings());
        var hasExternal = (ext.TvmazeEnabled != false)
                          || !string.IsNullOrWhiteSpace(ext.TmdbApiKey)
                          || !string.IsNullOrWhiteSpace(ext.FanartApiKey)
                          || (!string.IsNullOrWhiteSpace(ext.IgdbClientId)
                              && !string.IsNullOrWhiteSpace(ext.IgdbClientSecret));

        var ui = _settings.GetUi(new Models.Settings.UiSettings());
        var onboardingDone = ui.OnboardingDone;

        var shouldShow = !onboardingDone && (!hasExternal || sourcesCount == 0);

        return Ok(new
        {
            onboardingDone,
            hasSources = sourcesCount > 0,
            hasExternalProviders = hasExternal,
            shouldShow
        });
    }

    // POST /api/system/onboarding/complete
    [HttpPost("onboarding/complete")]
    public IActionResult CompleteOnboarding()
    {
        var ui = _settings.GetUi(new Models.Settings.UiSettings());
        if (!ui.OnboardingDone)
        {
            ui.OnboardingDone = true;
            _settings.SaveUi(ui);
        }

        return Ok(new { ok = true, onboardingDone = true });
    }

    // POST /api/system/onboarding/reset
    [HttpPost("onboarding/reset")]
    public IActionResult ResetOnboarding()
    {
        var ui = _settings.GetUi(new Models.Settings.UiSettings());
        ui.OnboardingDone = false;
        _settings.SaveUi(ui);

        return Ok(new { ok = true, onboardingDone = false });
    }

    // GET /api/system/backups
    [HttpGet("backups")]
    public IActionResult Backups()
    {
        return Ok(_backupService.ListBackups());
    }

    // GET /api/system/backups/state
    [HttpGet("backups/state")]
    public IActionResult BackupState()
    {
        return Ok(_backupService.GetOperationState());
    }

    // POST /api/system/backups/purge
    [HttpPost("backups/purge")]
    public IActionResult PurgeBackups()
    {
        var deleted = _backupService.PurgeBackups();
        return Ok(new { ok = true, deleted });
    }

    // POST /api/system/backups
    [HttpPost("backups")]
    public IActionResult CreateBackup()
    {
        try
        {
            var backup = _backupService.CreateBackup(GetAppVersion());
            return Ok(backup);
        }
        catch (BackupOperationException ex)
        {
            return ToBackupErrorResult(ex);
        }
    }

    // DELETE /api/system/backups/{name}
    [HttpDelete("backups/{name}")]
    public IActionResult DeleteBackup([FromRoute] string name)
    {
        try
        {
            _backupService.DeleteBackup(name);
            return Ok(new { ok = true });
        }
        catch (BackupOperationException ex)
        {
            return ToBackupErrorResult(ex);
        }
    }

    // GET /api/system/backups/{name}/download
    [HttpGet("backups/{name}/download")]
    public IActionResult DownloadBackup([FromRoute] string name)
    {
        try
        {
            var (safeName, path) = _backupService.GetExistingBackupFile(name);
            return PhysicalFile(path, "application/zip", safeName);
        }
        catch (BackupOperationException ex)
        {
            return ToBackupErrorResult(ex);
        }
    }

    // POST /api/system/backups/{name}/restore
    [HttpPost("backups/{name}/restore")]
    public IActionResult RestoreBackup([FromRoute] string name)
    {
        try
        {
            var result = _backupService.RestoreBackup(name, GetAppVersion());
            var warning = result.ClearedUndecryptableCredentials > 0
                ? "Certaines clés API chiffrées n'ont pas pu être déchiffrées et ont été supprimées."
                : null;

            return Ok(new
            {
                ok = true,
                needsRestart = true,
                reencryptedCredentials = result.ReencryptedCredentials,
                clearedUndecryptableCredentials = result.ClearedUndecryptableCredentials,
                warning
            });
        }
        catch (BackupOperationException ex)
        {
            return ToBackupErrorResult(ex);
        }
    }

    // GET /api/system/stats/summary - Lightweight data for dashboard tabs
    [EnableRateLimiting("stats-heavy")]
    [HttpGet("stats/summary")]
    public IActionResult StatsSummary()
    {
        const string cacheKey = "system:stats:summary:v1";
        if (_cache.TryGetValue<object>(cacheKey, out var cached) && cached is not null)
            return Ok(cached);

        using var conn = _db.Open();

        var activeIndexers = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM sources WHERE enabled = 1;");
        var releasesCount = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM releases;");

        var providerStats = _providerStats.Snapshot();
        var indexerStats = _providerStats.IndexerSnapshot();

        var totalCalls = providerStats.Tmdb.Calls + providerStats.Tvmaze.Calls
                       + providerStats.Fanart.Calls + providerStats.Igdb.Calls;
        var totalFailures = providerStats.Tmdb.Failures + providerStats.Tvmaze.Failures
                          + providerStats.Fanart.Failures + providerStats.Igdb.Failures;

        var storage = GetStorageUsageSnapshot();
        var localPosters = storage.PostersTopLevelCount;

        var missingPoster = conn.ExecuteScalar<int>(
            @"SELECT COUNT(1) FROM releases
              LEFT JOIN media_entities me ON me.id = releases.entity_id
              WHERE COALESCE(releases.poster_file, me.poster_file) IS NULL
                 OR COALESCE(releases.poster_file, me.poster_file) = '';");
        var matchingPercent = releasesCount > 0
            ? (int)Math.Round(((releasesCount - missingPoster) / (double)releasesCount) * 100)
            : 0;

        var payload = new
        {
            version = GetAppVersion(),
            uptimeSeconds = (long)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
            activeIndexers,
            totalQueries = indexerStats.Queries,
            totalCalls,
            totalFailures,
            releasesCount,
            matchingPercent = Math.Max(0, Math.Min(100, matchingPercent))
        };

        _cache.Set(cacheKey, payload, StatsCacheDuration);
        return Ok(payload);
    }

    // GET /api/system/stats/feedarr - Feedarr overview tab
    [EnableRateLimiting("stats-heavy")]
    [HttpGet("stats/feedarr")]
    public IActionResult StatsFeedarr([FromQuery] int days = 30)
    {
        days = days switch { 7 => 7, 90 => 90, _ => 30 };
        var cacheKey = $"system:stats:feedarr:v2:{days}";
        if (_cache.TryGetValue<object>(cacheKey, out var cached) && cached is not null)
            return Ok(cached);

        using var conn = _db.Open();

        var releasesCount = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM releases;");

        var storage = GetStorageUsageSnapshot();
        var dbSizeMb = Math.Round(storage.DatabaseBytes / 1024d / 1024d, 2);
        var localPosters = storage.PostersTopLevelCount;

        var missingPosters = conn.ExecuteScalar<int>(
            @"SELECT COUNT(1) FROM releases
              LEFT JOIN media_entities me ON me.id = releases.entity_id
              WHERE COALESCE(releases.poster_file, me.poster_file) IS NULL
                 OR COALESCE(releases.poster_file, me.poster_file) = '';");

        // Poster reuse stats
        var releasesWithPoster = releasesCount - missingPosters;
        var matchingPercent = releasesCount > 0
            ? (int)Math.Round(((double)releasesWithPoster / releasesCount) * 100)
            : 0;
        var distinctPosterFiles = conn.ExecuteScalar<int>(
            @"SELECT COUNT(DISTINCT COALESCE(releases.poster_file, me.poster_file))
              FROM releases
              LEFT JOIN media_entities me ON me.id = releases.entity_id
              WHERE COALESCE(releases.poster_file, me.poster_file) IS NOT NULL
                AND COALESCE(releases.poster_file, me.poster_file) != '';");
        var posterReuseRatio = distinctPosterFiles > 0
            ? Math.Round((double)releasesWithPoster / distinctPosterFiles, 1)
            : 0.0;

        var sinceTs = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();
        var releasesPerDay = conn.Query<(string date, int count)>(
            @"SELECT date(created_at_ts, 'unixepoch') as date, COUNT(*) as count
              FROM releases
              WHERE created_at_ts > @sinceTs
              GROUP BY date
              ORDER BY date",
            new { sinceTs }
        ).Select(r => new { date = r.date, count = r.count }).ToList();

        // Global arr match counts from the item list status cache
        var sonarrMatchCount = conn.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM release_arr_status WHERE in_sonarr = 1;");
        var radarrMatchCount = conn.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM release_arr_status WHERE in_radarr = 1;");

        // Storage breakdown (cached)
        var databaseBytes = storage.DatabaseBytes;
        var postersBytes = storage.PostersBytes;
        var backupsBytes = storage.BackupsBytes;

        // Arr apps stats (single query, no N+1)
        var arrApps = conn.Query<ArrAppStatsRow>(
            """
            SELECT
                a.id as Id,
                a.name as Name,
                a.type as Type,
                a.is_enabled as IsEnabled,
                COALESCE(li.library_count, 0) as LibraryCount,
                s.last_sync_at as LastSyncAt,
                COALESCE(s.last_sync_count, 0) as LastSyncCount,
                s.last_error as LastError
            FROM arr_applications a
            LEFT JOIN (
                SELECT app_id, COUNT(*) as library_count
                FROM arr_library_items
                GROUP BY app_id
            ) li ON li.app_id = a.id
            LEFT JOIN arr_sync_status s ON s.app_id = a.id
            ORDER BY a.type, a.name;
            """
        ).Select(row => new
        {
            id = row.Id,
            name = row.Name,
            type = row.Type,
            enabled = row.IsEnabled == 1,
            libraryCount = row.LibraryCount,
            lastSyncAt = row.LastSyncAt,
            lastSyncCount = row.LastSyncCount,
            lastError = row.LastError,
            displayCount = row.Type switch
            {
                "sonarr" => sonarrMatchCount,
                "radarr" => radarrMatchCount,
                "overseerr" => Math.Max(0, row.LastSyncCount),
                "jellyseerr" => Math.Max(0, row.LastSyncCount),
                "seer" => Math.Max(0, row.LastSyncCount),
                _ => 0
            },
            countMode = row.Type is "sonarr" or "radarr" ? "matches" : "requests"
        }).ToList();

        var payload = new
        {
            version = GetAppVersion(),
            uptimeSeconds = (long)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
            dbSizeMB = dbSizeMb,
            localPosters,
            releasesCount,
            missingPosters,
            releasesWithPoster,
            matchingPercent,
            distinctPosterFiles,
            posterReuseRatio,
            releasesPerDay,
            sonarrMatchedCount = sonarrMatchCount,
            radarrMatchedCount = radarrMatchCount,
            storage = new { databaseBytes, postersBytes, backupsBytes },
            arrApps
        };

        _cache.Set(cacheKey, payload, StatsCacheDuration);
        return Ok(payload);
    }

    // GET /api/system/stats/indexers - Indexers tab
    [EnableRateLimiting("stats-heavy")]
    [HttpGet("stats/indexers")]
    public IActionResult StatsIndexers()
    {
        const string cacheKey = "system:stats:indexers:v1";
        if (_cache.TryGetValue<object>(cacheKey, out var cached) && cached is not null)
            return Ok(cached);

        using var conn = _db.Open();

        var activeIndexers = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM sources WHERE enabled = 1;");
        var totalIndexers = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM sources;");
        var indexerStats = _providerStats.IndexerSnapshot();

        var sourceRows = GetSourceStatsRows(conn);

        var indexerStatsBySource = sourceRows
            .Where(r => r.Enabled == 1)
            .Select(r => new
            {
                id = r.Id,
                name = r.Name,
                releaseCount = r.ReleaseCount,
                lastStatus = r.LastStatus
            })
            .OrderByDescending(r => r.releaseCount)
            .ToList();

        var releasesByCategoryByIndexer = conn.Query<(long sourceId, string sourceName, int categoryId, int count)>(
            @"SELECT s.id as sourceId, s.name as sourceName, r.category_id as categoryId, COUNT(*) as count
              FROM releases r
              JOIN sources s ON r.source_id = s.id
              WHERE s.enabled = 1 AND r.category_id IS NOT NULL
              GROUP BY s.id, s.name, r.category_id
              ORDER BY s.name, count DESC"
        ).Select(r => new { sourceId = r.sourceId, sourceName = r.sourceName, categoryId = r.categoryId, count = r.count }).ToList();

        var indexerDetails = sourceRows
            .Select(r => new
            {
                id = r.Id,
                name = r.Name,
                enabled = r.Enabled == 1,
                releaseCount = r.ReleaseCount,
                lastSyncAtTs = r.LastSyncAtTs,
                lastStatus = r.LastStatus,
                lastError = r.LastError,
                lastItemCount = r.LastItemCount
            })
            .OrderByDescending(r => r.releaseCount)
            .ToList();

        var payload = new
        {
            activeIndexers, totalIndexers,
            queries = indexerStats.Queries, failures = indexerStats.Failures,
            syncJobs = indexerStats.SyncJobs, syncFailures = indexerStats.SyncFailures,
            indexerStatsBySource, releasesByCategoryByIndexer, indexerDetails
        };

        _cache.Set(cacheKey, payload, StatsCacheDuration);
        return Ok(payload);
    }

    // GET /api/system/stats/providers - Providers tab
    [EnableRateLimiting("stats-heavy")]
    [HttpGet("stats/providers")]
    public IActionResult StatsProviders()
    {
        var stats = _providerStats.Snapshot();

        return Ok(new
        {
            tmdb = new { calls = stats.Tmdb.Calls, failures = stats.Tmdb.Failures, avgMs = stats.Tmdb.AvgMs },
            tvmaze = new { calls = stats.Tvmaze.Calls, failures = stats.Tvmaze.Failures, avgMs = stats.Tvmaze.AvgMs },
            fanart = new { calls = stats.Fanart.Calls, failures = stats.Fanart.Failures, avgMs = stats.Fanart.AvgMs },
            igdb = new { calls = stats.Igdb.Calls, failures = stats.Igdb.Failures, avgMs = stats.Igdb.AvgMs }
        });
    }

    // GET /api/system/stats/releases - Releases tab
    [EnableRateLimiting("stats-heavy")]
    [HttpGet("stats/releases")]
    public IActionResult StatsReleases()
    {
        const string cacheKey = "system:stats:releases:v1";
        if (_cache.TryGetValue<object>(cacheKey, out var cached) && cached is not null)
            return Ok(cached);

        using var conn = _db.Open();

        var releasesCount = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM releases;");
        var withPoster = conn.ExecuteScalar<int>(
            """
            SELECT COUNT(1)
            FROM releases
            LEFT JOIN media_entities me ON me.id = releases.entity_id
            WHERE COALESCE(releases.poster_file, me.poster_file) IS NOT NULL
              AND COALESCE(releases.poster_file, me.poster_file) != '';
            """);
        var missingPoster = releasesCount - withPoster;

        var releasesByCategory = conn.Query<(int categoryId, int count)>(
            @"SELECT category_id, COUNT(*) as count
              FROM releases WHERE category_id IS NOT NULL
              GROUP BY category_id ORDER BY count DESC LIMIT 15"
        ).Select(r => new { categoryId = r.categoryId, count = r.count }).ToList();

        var sizeDistribution = conn.Query<(string range, int count)>(
            @"SELECT
                CASE
                  WHEN size_bytes IS NULL OR size_bytes <= 0 THEN 'Inconnu'
                  WHEN size_bytes < 1073741824 THEN '< 1 Go'
                  WHEN size_bytes < 5368709120 THEN '1-5 Go'
                  WHEN size_bytes < 16106127360 THEN '5-15 Go'
                  WHEN size_bytes < 53687091200 THEN '15-50 Go'
                  ELSE '> 50 Go'
                END as range,
                COUNT(*) as count
              FROM releases
              GROUP BY range
              ORDER BY MIN(COALESCE(size_bytes, 0))"
        ).Select(r => new { range = r.range, count = r.count }).ToList();

        var seedersDistribution = conn.Query<(string range, int count)>(
            @"SELECT
                CASE
                  WHEN seeders IS NULL THEN 'Inconnu'
                  WHEN seeders = 0 THEN '0'
                  WHEN seeders <= 10 THEN '1-10'
                  WHEN seeders <= 50 THEN '11-50'
                  WHEN seeders <= 200 THEN '51-200'
                  ELSE '200+'
                END as range,
                COUNT(*) as count
              FROM releases
              GROUP BY range
              ORDER BY MIN(COALESCE(seeders, -1))"
        ).Select(r => new { range = r.range, count = r.count }).ToList();

        var topGrabbed = conn.Query<(string title, int grabs, int? seeders, long? sizeBytes, int? categoryId)>(
            @"SELECT title, grabs, seeders, size_bytes as sizeBytes, category_id as categoryId
              FROM releases
              WHERE grabs IS NOT NULL AND grabs > 0
              ORDER BY grabs DESC
              LIMIT 20"
        ).Select(r => new { title = r.title, grabs = r.grabs, seeders = r.seeders ?? 0, sizeBytes = r.sizeBytes ?? 0, categoryId = r.categoryId ?? 0 }).ToList();

        var payload = new
        {
            releasesCount, withPoster, missingPoster,
            releasesByCategory, sizeDistribution, seedersDistribution, topGrabbed
        };

        _cache.Set(cacheKey, payload, StatsCacheDuration);
        return Ok(payload);
    }

    // GET /api/system/stats - Extended statistics for dashboard (legacy)
    [EnableRateLimiting("stats-heavy")]
    [HttpGet("stats")]
    public IActionResult Stats()
    {
        const string cacheKey = "system:stats:legacy:v1";
        if (_cache.TryGetValue<object>(cacheKey, out var cached) && cached is not null)
            return Ok(cached);

        using var conn = _db.Open();

        // Basic counts
        var activeIndexers = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM sources WHERE enabled = 1;");
        var totalIndexers = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM sources;");
        var releasesCount = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM releases;");

        var storage = GetStorageUsageSnapshot();
        var dbSizeMb = Math.Round(storage.DatabaseBytes / 1024d / 1024d, 2);
        var localPosters = storage.PostersTopLevelCount;

        // Provider stats
        var providerStats = _providerStats.Snapshot();
        var indexerStats = _providerStats.IndexerSnapshot();

        // Releases by category (for charts)
        var releasesByCategory = conn.Query<(int categoryId, int count)>(
            @"SELECT category_id, COUNT(*) as count
              FROM releases
              WHERE category_id IS NOT NULL
              GROUP BY category_id
              ORDER BY count DESC
              LIMIT 10"
        ).Select(r => new { categoryId = r.categoryId, count = r.count }).ToList();

        // Indexer stats by source (for charts)
        var sourceRows = GetSourceStatsRows(conn);
        var indexerStatsBySource = sourceRows
            .Where(r => r.Enabled == 1)
            .Select(r => new
            {
                id = r.Id,
                name = r.Name,
                releaseCount = r.ReleaseCount,
                lastStatus = r.LastStatus
            })
            .OrderByDescending(r => r.releaseCount)
            .ToList();

        // Releases by category by indexer (for stacked bar chart)
        var releasesByCategoryByIndexer = conn.Query<(long sourceId, string sourceName, int categoryId, int count)>(
            @"SELECT s.id as sourceId, s.name as sourceName, r.category_id as categoryId, COUNT(*) as count
              FROM releases r
              JOIN sources s ON r.source_id = s.id
              WHERE s.enabled = 1 AND r.category_id IS NOT NULL
              GROUP BY s.id, s.name, r.category_id
              ORDER BY s.name, count DESC"
        ).Select(r => new {
            sourceId = r.sourceId,
            sourceName = r.sourceName,
            categoryId = r.categoryId,
            count = r.count
        }).ToList();

        var payload = new
        {
            // Summary cards
            activeIndexers,
            totalIndexers,
            totalQueries = indexerStats.Queries,
            totalSyncJobs = indexerStats.SyncJobs,
            dbSizeMB = dbSizeMb,
            localPosters,
            releasesCount,

            // Provider stats
            providers = new
            {
                tmdb = new { calls = providerStats.Tmdb.Calls, failures = providerStats.Tmdb.Failures, avgMs = providerStats.Tmdb.AvgMs },
                tvmaze = new { calls = providerStats.Tvmaze.Calls, failures = providerStats.Tvmaze.Failures, avgMs = providerStats.Tvmaze.AvgMs },
                fanart = new { calls = providerStats.Fanart.Calls, failures = providerStats.Fanart.Failures, avgMs = providerStats.Fanart.AvgMs },
                igdb = new { calls = providerStats.Igdb.Calls, failures = providerStats.Igdb.Failures, avgMs = providerStats.Igdb.AvgMs }
            },

            // Indexer stats
            indexers = new
            {
                queries = indexerStats.Queries,
                failures = indexerStats.Failures,
                syncJobs = indexerStats.SyncJobs,
                syncFailures = indexerStats.SyncFailures
            },

            // Chart data
            releasesByCategory,
            indexerStatsBySource,
            releasesByCategoryByIndexer
        };

        _cache.Set(cacheKey, payload, StatsCacheDuration);
        return Ok(payload);
    }

    private static List<SourceStatsRow> GetSourceStatsRows(IDbConnection conn)
    {
        return conn.Query<SourceStatsRow>(
            """
            SELECT s.id as Id,
                   s.name as Name,
                   s.enabled as Enabled,
                   s.last_sync_at_ts as LastSyncAtTs,
                   COALESCE(s.last_status, '') as LastStatus,
                   s.last_error as LastError,
                   COALESCE(s.last_item_count, 0) as LastItemCount,
                   COALESCE(rc.cnt, 0) as ReleaseCount
            FROM sources s
            LEFT JOIN (
                SELECT source_id, COUNT(1) as cnt
                FROM releases
                GROUP BY source_id
            ) rc ON rc.source_id = s.id;
            """
        ).ToList();
    }

    // GET /api/system/storage
    [HttpGet("storage")]
    public IActionResult Storage()
    {
        var volumes = new List<DiskVolumeDto>();
        var isLinux = !OperatingSystem.IsWindows();

        if (isLinux)
        {
            // On Linux/Docker, show only relevant paths (DataDir and common mount points)
            var pathsToCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                DataDirAbs,
                "/",
                "/data",
                "/config",
                "/downloads",
                "/media",
                "/tv",
                "/movies"
            };

            // Get unique mount points for these paths
            var seenMounts = new HashSet<string>();
            foreach (var path in pathsToCheck)
            {
                try
                {
                    if (!Directory.Exists(path) && !System.IO.File.Exists(path)) continue;

                    var driveInfo = new DriveInfo(path);
                    if (!driveInfo.IsReady) continue;

                    // Use mount point as unique key
                    var mountKey = driveInfo.RootDirectory.FullName;
                    if (seenMounts.Contains(mountKey)) continue;
                    seenMounts.Add(mountKey);

                    volumes.Add(new DiskVolumeDto
                    {
                        Path = path == "/" ? "/" : path.TrimEnd('/'),
                        Label = string.IsNullOrWhiteSpace(driveInfo.VolumeLabel) ? path : driveInfo.VolumeLabel,
                        FreeBytes = driveInfo.AvailableFreeSpace,
                        TotalBytes = driveInfo.TotalSize
                    });
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Skipping inaccessible storage path {Path}", path);
                }
            }
        }
        else
        {
            // On Windows, use standard drive enumeration
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Network))
                .ToList();

            foreach (var drive in drives)
            {
                try
                {
                    volumes.Add(new DiskVolumeDto
                    {
                        Path = drive.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        Label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : drive.VolumeLabel,
                        FreeBytes = drive.AvailableFreeSpace,
                        TotalBytes = drive.TotalSize
                    });
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Skipping inaccessible drive {Drive}", drive.Name);
                }
            }
        }

        var storage = GetStorageUsageSnapshot();

        // Calculate usage
        var usage = new StorageUsageDto();
        usage.DatabaseBytes = storage.DatabaseBytes;
        usage.PostersCount = storage.PostersRecursiveCount;
        usage.PostersBytes = storage.PostersBytes;
        usage.BackupsCount = storage.BackupsCount;
        usage.BackupsBytes = storage.BackupsBytes;

        return Ok(new StorageInfoDto
        {
            Volumes = volumes,
            Usage = usage
        });
    }

    private string GetAppVersion()
    {
        var envVersion = Environment.GetEnvironmentVariable("FEEDARR_VERSION");
        var asm = typeof(Program).Assembly;
        var infoVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var asmVersion = asm.GetName().Version?.ToString();

        if (TryExtractSemVer(envVersion, out var parsedEnv))
            return parsedEnv;
        if (TryExtractSemVer(infoVersion, out var parsedInfo))
            return parsedInfo;
        if (TryExtractSemVer(asmVersion, out var parsedAsm))
            return parsedAsm;

        return "0.0.0";
    }

    private static bool TryExtractSemVer(string? value, out string version)
    {
        version = "";
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var match = _semVerRegex.Match(value);
        if (!match.Success)
            return false;

        var major = match.Groups["major"].Value;
        var minor = match.Groups["minor"].Value;
        var patch = match.Groups["patch"].Value;
        version = $"{major}.{minor}.{patch}";
        return true;
    }

    private IActionResult ToBackupErrorResult(BackupOperationException ex)
    {
        if (ex.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            _log.LogError(ex, "Backup operation failed with status code {StatusCode}", ex.StatusCode);
            return StatusCode(ex.StatusCode, new { error = "internal server error" });
        }

        var safeError = ErrorMessageSanitizer.ToOperationalMessage(ex, "backup operation failed");
        return ex.StatusCode switch
        {
            StatusCodes.Status400BadRequest => BadRequest(new { error = safeError }),
            StatusCodes.Status404NotFound => NotFound(new { error = safeError }),
            StatusCodes.Status409Conflict => Conflict(new { error = safeError }),
            _ => StatusCode(ex.StatusCode, new { error = safeError })
        };
    }

    private StorageUsageSnapshot GetStorageUsageSnapshot()
    {
        if (_cache.TryGetValue(StorageUsageCacheKey, out StorageUsageSnapshot? cached) && cached is not null)
            return cached;

        StartStorageUsageRefresh();

        lock (StorageRefreshLock)
        {
            if (HasStorageSnapshot)
                return LastKnownStorageUsage;
        }

        var seeded = ComputeStorageUsageSnapshot();
        lock (StorageRefreshLock)
        {
            LastKnownStorageUsage = seeded;
            HasStorageSnapshot = true;
        }
        _cache.Set(StorageUsageCacheKey, seeded, StorageUsageCacheDuration);
        return seeded;
    }

    private void StartStorageUsageRefresh()
    {
        lock (StorageRefreshLock)
        {
            if (StorageRefreshTask is not null && !StorageRefreshTask.IsCompleted)
                return;

            StorageRefreshTask = Task.Run(() =>
            {
                try
                {
                    var snapshot = ComputeStorageUsageSnapshot();
                    lock (StorageRefreshLock)
                    {
                        LastKnownStorageUsage = snapshot;
                        HasStorageSnapshot = true;
                    }
                    _cache.Set(StorageUsageCacheKey, snapshot, StorageUsageCacheDuration);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Background storage usage refresh failed");
                }
            });
        }
    }

    private StorageUsageSnapshot ComputeStorageUsageSnapshot()
    {
        long databaseBytes = 0;
        int postersTopLevelCount = 0;
        int postersRecursiveCount = 0;
        long postersBytes = 0;
        int backupsCount = 0;
        long backupsBytes = 0;

        try
        {
            if (System.IO.File.Exists(DbPathAbs))
                databaseBytes = new FileInfo(DbPathAbs).Length;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read database size");
        }

        var postersDir = Path.Combine(DataDirAbs, "posters");
        try
        {
            if (Directory.Exists(postersDir))
            {
                postersTopLevelCount = Directory.GetFiles(postersDir, "*.*", SearchOption.TopDirectoryOnly).Length;
                var files = Directory.GetFiles(postersDir, "*.*", SearchOption.AllDirectories);
                postersRecursiveCount = files.Length;
                postersBytes = files.Sum(f => new FileInfo(f).Length);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to calculate posters usage in {PostersDir}", postersDir);
        }

        try
        {
            if (Directory.Exists(BackupDirAbs))
            {
                var files = Directory.GetFiles(BackupDirAbs, "*.zip", SearchOption.TopDirectoryOnly);
                backupsCount = files.Length;
                backupsBytes = files.Sum(f => new FileInfo(f).Length);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to calculate backups usage in {BackupDir}", BackupDirAbs);
        }

        return new StorageUsageSnapshot(
            databaseBytes,
            postersTopLevelCount,
            postersRecursiveCount,
            postersBytes,
            backupsCount,
            backupsBytes
        );
    }

    private sealed class ArrAppStatsRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public int IsEnabled { get; set; }
        public int LibraryCount { get; set; }
        public string? LastSyncAt { get; set; }
        public int LastSyncCount { get; set; }
        public string? LastError { get; set; }
    }

    private sealed record StorageUsageSnapshot(
        long DatabaseBytes,
        int PostersTopLevelCount,
        int PostersRecursiveCount,
        long PostersBytes,
        int BackupsCount,
        long BackupsBytes);
}
