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

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/system")]
public sealed class SystemController : ControllerBase
{
    private static readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private static readonly Regex _semVerRegex = new(
        @"(?<!\d)v?(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
    );

    private readonly Db _db;
    private readonly IWebHostEnvironment _env;
    private readonly AppOptions _opts;
    private readonly SettingsRepository _settings;
    private readonly ProviderStatsService _providerStats;
    private readonly BackupService _backupService;
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
        BackupService backupService,
        ILogger<SystemController> log)
    {
        _db = db;
        _env = env;
        _opts = opts.Value;
        _settings = settings;
        _providerStats = providerStats;
        _backupService = backupService;
        _log = log;
    }

    [HttpGet("status")]
    public IActionResult Status([FromQuery] long? releasesSinceTs = null)
    {
        using var conn = _db.Open();

        var sourcesCount = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM sources;");
        var releasesCount = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM releases;");
        var releasesLatestTs = conn.ExecuteScalar<long?>("SELECT MAX(created_at_ts) FROM releases;");
        var safeSinceTs = Math.Max(0L, releasesSinceTs ?? 0L);
        int? releasesNewSinceTsCount = safeSinceTs > 0
            ? conn.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM releases WHERE created_at_ts > @sinceTs;",
                new { sinceTs = safeSinceTs })
            : null;

        long? lastSyncAt = conn.ExecuteScalar<long?>(
            "SELECT MAX(last_sync_at_ts) FROM sources;"
        );

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
            DataDir = _opts.DataDir ?? "data",
            DbPath = _db.DbPath,
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
    [HttpGet("stats/summary")]
    public IActionResult StatsSummary()
    {
        using var conn = _db.Open();

        var activeIndexers = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM sources WHERE enabled = 1;");
        var releasesCount = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM releases;");

        var providerStats = _providerStats.Snapshot();
        var indexerStats = _providerStats.IndexerSnapshot();

        var totalCalls = providerStats.Tmdb.Calls + providerStats.Tvmaze.Calls
                       + providerStats.Fanart.Calls + providerStats.Igdb.Calls;
        var totalFailures = providerStats.Tmdb.Failures + providerStats.Tvmaze.Failures
                          + providerStats.Fanart.Failures + providerStats.Igdb.Failures;

        var postersDir = Path.Combine(_opts.DataDir ?? "data", "posters");
        var localPosters = 0;
        try { if (Directory.Exists(postersDir)) localPosters = Directory.GetFiles(postersDir, "*.*", SearchOption.TopDirectoryOnly).Length; }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to count local posters"); }

        var missingPoster = conn.ExecuteScalar<int>(
            @"SELECT COUNT(1) FROM releases
              LEFT JOIN media_entities me ON me.id = releases.entity_id
              WHERE COALESCE(releases.poster_file, me.poster_file) IS NULL
                 OR COALESCE(releases.poster_file, me.poster_file) = '';");
        var matchingPercent = releasesCount > 0
            ? (int)Math.Round(((releasesCount - missingPoster) / (double)releasesCount) * 100)
            : 0;

        return Ok(new
        {
            version = GetAppVersion(),
            uptimeSeconds = (long)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
            activeIndexers,
            totalQueries = indexerStats.Queries,
            totalCalls,
            totalFailures,
            releasesCount,
            matchingPercent = Math.Max(0, Math.Min(100, matchingPercent))
        });
    }

    // GET /api/system/stats/feedarr - Feedarr overview tab
    [HttpGet("stats/feedarr")]
    public IActionResult StatsFeedarr([FromQuery] int days = 30)
    {
        days = days switch { 7 => 7, 90 => 90, _ => 30 };

        using var conn = _db.Open();

        var releasesCount = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM releases;");

        var dbSizeMb = 0.0;
        try
        {
            if (System.IO.File.Exists(_db.DbPath))
                dbSizeMb = Math.Round(new FileInfo(_db.DbPath).Length / 1024d / 1024d, 2);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to read database size"); }

        var postersDir = Path.Combine(_opts.DataDir ?? "data", "posters");
        var localPosters = 0;
        try { if (Directory.Exists(postersDir)) localPosters = Directory.GetFiles(postersDir, "*.*", SearchOption.TopDirectoryOnly).Length; }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to count local posters"); }

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

        // Storage breakdown
        long databaseBytes = 0, postersBytes = 0, backupsBytes = 0;
        try { if (System.IO.File.Exists(DbPathAbs)) databaseBytes = new FileInfo(DbPathAbs).Length; }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to read database file size"); }
        try
        {
            var pd = Path.Combine(DataDirAbs, "posters");
            if (Directory.Exists(pd))
                postersBytes = Directory.GetFiles(pd, "*.*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to calculate posters directory size"); }
        try
        {
            if (Directory.Exists(BackupDirAbs))
                backupsBytes = Directory.GetFiles(BackupDirAbs, "*.zip", SearchOption.TopDirectoryOnly).Sum(f => new FileInfo(f).Length);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to calculate backups directory size"); }

        // Arr apps stats
        var arrApps = conn.Query<(long id, string name, string type, int isEnabled)>(
            "SELECT id, name, type, is_enabled FROM arr_applications ORDER BY type, name"
        ).Select(a =>
        {
            var itemCount = conn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM arr_library_items WHERE app_id = @id",
                new { id = a.id });
            var syncRow = conn.QueryFirstOrDefault<(string lastSyncAt, int? lastSyncCount, string lastError)>(
                "SELECT last_sync_at, last_sync_count, last_error FROM arr_sync_status WHERE app_id = @id",
                new { id = a.id });
            return new
            {
                id = a.id, name = a.name, type = a.type,
                enabled = a.isEnabled == 1,
                libraryCount = itemCount,
                lastSyncAt = syncRow.lastSyncAt,
                lastSyncCount = syncRow.lastSyncCount ?? 0,
                lastError = syncRow.lastError
            };
        }).ToList();

        return Ok(new
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
            storage = new { databaseBytes, postersBytes, backupsBytes },
            arrApps
        });
    }

    // GET /api/system/stats/indexers - Indexers tab
    [HttpGet("stats/indexers")]
    public IActionResult StatsIndexers()
    {
        using var conn = _db.Open();

        var activeIndexers = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM sources WHERE enabled = 1;");
        var totalIndexers = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM sources;");
        var indexerStats = _providerStats.IndexerSnapshot();

        var indexerStatsBySource = conn.Query<(long id, string name, int releaseCount, string lastStatus)>(
            @"SELECT s.id, s.name,
                     (SELECT COUNT(*) FROM releases r WHERE r.source_id = s.id) as releaseCount,
                     COALESCE(s.last_status, '') as lastStatus
              FROM sources s
              WHERE s.enabled = 1
              ORDER BY releaseCount DESC"
        ).Select(r => new { id = r.id, name = r.name, releaseCount = r.releaseCount, lastStatus = r.lastStatus }).ToList();

        var releasesByCategoryByIndexer = conn.Query<(long sourceId, string sourceName, int categoryId, int count)>(
            @"SELECT s.id as sourceId, s.name as sourceName, r.category_id as categoryId, COUNT(*) as count
              FROM releases r
              JOIN sources s ON r.source_id = s.id
              WHERE s.enabled = 1 AND r.category_id IS NOT NULL
              GROUP BY s.id, s.name, r.category_id
              ORDER BY s.name, count DESC"
        ).Select(r => new { sourceId = r.sourceId, sourceName = r.sourceName, categoryId = r.categoryId, count = r.count }).ToList();

        var indexerDetails = conn.Query<(long id, string name, int enabled, int releaseCount, long? lastSyncAtTs, string lastStatus, string lastError, int lastItemCount)>(
            @"SELECT s.id, s.name, s.enabled,
                     (SELECT COUNT(*) FROM releases r WHERE r.source_id = s.id) as releaseCount,
                     s.last_sync_at_ts as lastSyncAtTs,
                     COALESCE(s.last_status, '') as lastStatus,
                     s.last_error as lastError,
                     COALESCE(s.last_item_count, 0) as lastItemCount
              FROM sources s
              ORDER BY releaseCount DESC"
        ).Select(r => new
        {
            id = r.id, name = r.name, enabled = r.enabled == 1,
            releaseCount = r.releaseCount, lastSyncAtTs = r.lastSyncAtTs,
            lastStatus = r.lastStatus, lastError = r.lastError, lastItemCount = r.lastItemCount
        }).ToList();

        return Ok(new
        {
            activeIndexers, totalIndexers,
            queries = indexerStats.Queries, failures = indexerStats.Failures,
            syncJobs = indexerStats.SyncJobs, syncFailures = indexerStats.SyncFailures,
            indexerStatsBySource, releasesByCategoryByIndexer, indexerDetails
        });
    }

    // GET /api/system/stats/providers - Providers tab
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
    [HttpGet("stats/releases")]
    public IActionResult StatsReleases()
    {
        using var conn = _db.Open();

        var releasesCount = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM releases;");
        var withPoster = conn.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM releases WHERE poster_file IS NOT NULL AND poster_file != '';");
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

        return Ok(new
        {
            releasesCount, withPoster, missingPoster,
            releasesByCategory, sizeDistribution, seedersDistribution, topGrabbed
        });
    }

    // GET /api/system/stats - Extended statistics for dashboard (legacy)
    [HttpGet("stats")]
    public IActionResult Stats()
    {
        using var conn = _db.Open();

        // Basic counts
        var activeIndexers = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM sources WHERE enabled = 1;");
        var totalIndexers = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM sources;");
        var releasesCount = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM releases;");

        // Database size
        var dbSizeMb = 0.0;
        try
        {
            if (System.IO.File.Exists(_db.DbPath))
            {
                var bytes = new FileInfo(_db.DbPath).Length;
                dbSizeMb = Math.Round(bytes / 1024d / 1024d, 2);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read database size for stats endpoint");
        }

        // Poster count
        var postersDir = Path.Combine(_opts.DataDir ?? "data", "posters");
        var localPosters = 0;
        try
        {
            if (Directory.Exists(postersDir))
            {
                localPosters = Directory.GetFiles(postersDir, "*.*", SearchOption.TopDirectoryOnly).Length;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to count local posters for stats endpoint");
        }

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
        var indexerStatsBySource = conn.Query<(long id, string name, int releaseCount, string lastStatus)>(
            @"SELECT s.id, s.name,
                     (SELECT COUNT(*) FROM releases r WHERE r.source_id = s.id) as releaseCount,
                     COALESCE(s.last_status, '') as lastStatus
              FROM sources s
              WHERE s.enabled = 1
              ORDER BY releaseCount DESC"
        ).Select(r => new {
            id = r.id,
            name = r.name,
            releaseCount = r.releaseCount,
            lastStatus = r.lastStatus
        }).ToList();

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

        return Ok(new
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
        });
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

        // Calculate usage
        var usage = new StorageUsageDto();

        // Database size
        try
        {
            if (System.IO.File.Exists(DbPathAbs))
                usage.DatabaseBytes = new FileInfo(DbPathAbs).Length;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read database size for storage endpoint");
        }

        // Posters folder
        var postersDir = Path.Combine(DataDirAbs, "posters");
        try
        {
            if (Directory.Exists(postersDir))
            {
                var files = Directory.GetFiles(postersDir, "*.*", SearchOption.AllDirectories);
                usage.PostersCount = files.Length;
                usage.PostersBytes = files.Sum(f => new FileInfo(f).Length);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to calculate posters storage usage in {PostersDir}", postersDir);
        }

        // Backups folder
        try
        {
            if (Directory.Exists(BackupDirAbs))
            {
                var files = Directory.GetFiles(BackupDirAbs, "*.zip", SearchOption.TopDirectoryOnly);
                usage.BackupsCount = files.Length;
                usage.BackupsBytes = files.Sum(f => new FileInfo(f).Length);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to calculate backups storage usage in {BackupDir}", BackupDirAbs);
        }

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

        return ex.StatusCode switch
        {
            StatusCodes.Status400BadRequest => BadRequest(new { error = ex.Message }),
            StatusCodes.Status404NotFound => NotFound(new { error = ex.Message }),
            StatusCodes.Status409Conflict => Conflict(new { error = ex.Message }),
            _ => StatusCode(ex.StatusCode, new { error = ex.Message })
        };
    }
}
