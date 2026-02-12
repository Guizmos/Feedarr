using Microsoft.AspNetCore.Mvc;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Fanart;
using Feedarr.Api.Services.Igdb;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Tmdb;
using Feedarr.Api.Services.TvMaze;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/maintenance")]
public sealed class MaintenanceController : ControllerBase
{
    private static readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    private readonly Db _db;
    private readonly ReleaseRepository _releases;
    private readonly ActivityRepository _activity;
    private readonly SettingsRepository _settings;
    private readonly PosterFetchService _posterFetch;
    private readonly RetroFetchLogService _retroLogs;
    private readonly TmdbClient _tmdb;
    private readonly TvMazeClient _tvmaze;
    private readonly FanartClient _fanart;
    private readonly IgdbClient _igdb;
    private readonly AppOptions _opts;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<MaintenanceController> _log;

    private string DataDirAbs =>
        Path.IsPathRooted(_opts.DataDir)
            ? _opts.DataDir
            : Path.GetFullPath(Path.Combine(_env.ContentRootPath, _opts.DataDir));

    public MaintenanceController(
        Db db,
        ReleaseRepository releases,
        ActivityRepository activity,
        SettingsRepository settings,
        PosterFetchService posterFetch,
        RetroFetchLogService retroLogs,
        TmdbClient tmdb,
        TvMazeClient tvmaze,
        FanartClient fanart,
        IgdbClient igdb,
        IOptions<AppOptions> opts,
        IWebHostEnvironment env,
        ILogger<MaintenanceController> log)
    {
        _db = db;
        _releases = releases;
        _activity = activity;
        _settings = settings;
        _posterFetch = posterFetch;
        _retroLogs = retroLogs;
        _tmdb = tmdb;
        _tvmaze = tvmaze;
        _fanart = fanart;
        _igdb = igdb;
        _opts = opts.Value;
        _env = env;
        _log = log;
    }

    // POST /api/maintenance/vacuum
    [HttpPost("vacuum")]
    public IActionResult Vacuum()
    {
        var dbPath = _db.DbPath;
        if (!System.IO.File.Exists(dbPath))
            return NotFound(new { error = "database not found" });

        double sizeBefore = 0;
        try { sizeBefore = Math.Round(new FileInfo(dbPath).Length / 1024d / 1024d, 2); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to read DB size before VACUUM"); }

        try
        {
            using var conn = _db.Open();
            conn.Execute("PRAGMA wal_checkpoint(TRUNCATE);");
            conn.Execute("VACUUM;");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "VACUUM maintenance task failed");
            return StatusCode(500, new { error = "internal server error" });
        }

        double sizeAfter = 0;
        try { sizeAfter = Math.Round(new FileInfo(dbPath).Length / 1024d / 1024d, 2); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to read DB size after VACUUM"); }

        var saved = Math.Round(sizeBefore - sizeAfter, 2);
        _activity.Add(null, "info", "maintenance", "Database optimized (VACUUM)",
            dataJson: $"{{\"sizeBefore\":{sizeBefore},\"sizeAfter\":{sizeAfter},\"savedMB\":{saved}}}");

        return Ok(new { ok = true, dbSizeBefore = sizeBefore, dbSizeAfter = sizeAfter, savedMB = saved });
    }

    // POST /api/maintenance/purge-logs
    public sealed class PurgeLogsDto
    {
        public string? Scope { get; set; }
        public int? OlderThanDays { get; set; }
    }

    [HttpPost("purge-logs")]
    public IActionResult PurgeLogs([FromBody] PurgeLogsDto? dto)
    {
        var scope = string.IsNullOrWhiteSpace(dto?.Scope) ? "all" : dto.Scope.Trim().ToLowerInvariant();
        if (scope is not ("all" or "history" or "logs"))
            return BadRequest(new { error = "invalid scope" });

        int deleted;
        if (dto?.OlderThanDays is > 0)
        {
            deleted = _activity.PurgeOlderThan(dto.OlderThanDays.Value, scope);
        }
        else
        {
            switch (scope)
            {
                case "all": _activity.PurgeAll(); break;
                case "history": _activity.PurgeHistory(); break;
                case "logs": _activity.PurgeLogs(); break;
            }
            deleted = -1; // full purge, count unknown
        }

        // Also delete retro-fetch CSV log files when purging all or logs
        var csvDeleted = 0;
        if (scope is "all" or "logs" && dto?.OlderThanDays is null or <= 0)
            csvDeleted = _retroLogs.PurgeLogFiles();

        _activity.Add(null, "info", "maintenance", "Logs purged",
            dataJson: $"{{\"scope\":\"{scope}\",\"olderThanDays\":{dto?.OlderThanDays ?? 0},\"deleted\":{deleted},\"csvDeleted\":{csvDeleted}}}");

        return Ok(new { ok = true, deleted, scope, olderThanDays = dto?.OlderThanDays, csvDeleted });
    }

    // GET /api/maintenance/stats
    [HttpGet("stats")]
    public IActionResult Stats()
    {
        using var conn = _db.Open();

        var releasesCount = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM releases;");
        var sourcesCount = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM sources;");
        var activeSourcesCount = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM sources WHERE enabled = 1;");
        var matchCacheCount = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM poster_matches;");
        var activityCount = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM activity_log;");
        var mediaEntityCount = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM media_entities;");
        var lastSyncTs = conn.ExecuteScalar<long?>("SELECT MAX(last_sync_at_ts) FROM sources;");
        var missingPosterCount = _releases.GetMissingPosterCount();

        var releasesPerCategory = conn.Query<(string? category, int count)>(
            """
            SELECT unified_category as category, COUNT(1) as count
            FROM releases
            WHERE unified_category IS NOT NULL AND unified_category <> ''
            GROUP BY unified_category
            ORDER BY count DESC;
            """
        ).Select(r => new { category = r.category, count = r.count }).ToList();

        // Database size
        double dbSizeMB = 0;
        try
        {
            if (System.IO.File.Exists(_db.DbPath))
                dbSizeMB = Math.Round(new FileInfo(_db.DbPath).Length / 1024d / 1024d, 2);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to read database size"); }

        // Posters info
        var postersDir = _posterFetch.PostersDirPath;
        var posterCount = 0;
        double posterSizeMB = 0;
        var orphanedPosterCount = 0;
        try
        {
            if (Directory.Exists(postersDir))
            {
                var files = Directory.GetFiles(postersDir, "*.*", SearchOption.TopDirectoryOnly);
                posterCount = files.Length;
                posterSizeMB = Math.Round(files.Sum(f => new FileInfo(f).Length) / 1024d / 1024d, 2);
                var localPosterFiles = files
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var referencedPosterFiles = _releases.GetReferencedPosterFiles();
                orphanedPosterCount = localPosterFiles.Count(file => !referencedPosterFiles.Contains(file));
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to enumerate poster directory for stats"); }

        // Duplicate count
        var duplicateCount = conn.ExecuteScalar<int>(
            """
            SELECT COUNT(1) FROM (
                WITH ranked AS (
                    SELECT id,
                           ROW_NUMBER() OVER (
                               PARTITION BY source_id, title_clean
                               ORDER BY COALESCE(published_at_ts, created_at_ts) DESC, id DESC
                           ) AS rn
                    FROM releases
                    WHERE title_clean IS NOT NULL AND title_clean <> ''
                )
                SELECT id FROM ranked WHERE rn > 1
            );
            """
        );

        return Ok(new
        {
            releasesCount,
            releasesPerCategory,
            sourcesCount,
            activeSourcesCount,
            dbSizeMB,
            posterCount,
            posterSizeMB,
            missingPosterCount,
            orphanedPosterCount,
            matchCacheCount,
            activityCount,
            mediaEntityCount,
            duplicateCount,
            lastSyncTs,
            uptimeSeconds = (long)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds
        });
    }

    // POST /api/maintenance/cleanup-posters
    [HttpPost("cleanup-posters")]
    public IActionResult CleanupPosters()
    {
        var postersDir = _posterFetch.PostersDirPath;
        if (!Directory.Exists(postersDir))
            return Ok(new { ok = true, scanned = 0, orphaned = 0, deleted = 0, freedBytes = 0L });

        var files = Directory.GetFiles(postersDir, "*.*", SearchOption.TopDirectoryOnly);
        var fileNames = files
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToList();
        var referencedPosters = _releases.GetReferencedPosterFiles(fileNames);
        var scanned = files.Length;
        var orphaned = 0;
        var deleted = 0;
        long freedBytes = 0;

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            if (string.IsNullOrWhiteSpace(fileName))
                continue;

            if (referencedPosters.Contains(fileName))
                continue;

            orphaned++;
            try
            {
                var size = new FileInfo(file).Length;
                var full = Path.GetFullPath(file);
                var root = Path.GetFullPath(postersDir);
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    continue;

                System.IO.File.Delete(file);
                if (System.IO.File.Exists(file))
                {
                    _log.LogWarning("Poster file still exists after delete attempt, skipping DB cleanup for {File}", fileName);
                    continue;
                }

                freedBytes += size;
                deleted++;
                _releases.ClearPosterFileReferences(fileName);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to delete orphaned poster: {File}", fileName);
            }
        }

        _activity.Add(null, "info", "maintenance", "Orphaned posters cleaned",
            dataJson: $"{{\"scanned\":{scanned},\"orphaned\":{orphaned},\"deleted\":{deleted},\"freedBytes\":{freedBytes}}}");

        return Ok(new { ok = true, scanned, orphaned, deleted, freedBytes });
    }

    // POST /api/maintenance/test-providers
    [HttpPost("test-providers")]
    public async Task<IActionResult> TestProviders(CancellationToken ct)
    {
        var ext = _settings.GetExternal(new ExternalSettings());
        var results = new List<object>();

        var tasks = new List<(string provider, Func<Task<bool>> test)>();

        if (!string.IsNullOrWhiteSpace(ext.TmdbApiKey) && ext.TmdbEnabled != false)
            tasks.Add(("tmdb", () => _tmdb.TestApiKeyAsync(ct)));
        if (ext.TvmazeEnabled != false)
            tasks.Add(("tvmaze", () => _tvmaze.TestApiAsync(ct)));
        if (!string.IsNullOrWhiteSpace(ext.FanartApiKey) && ext.FanartEnabled != false)
            tasks.Add(("fanart", () => _fanart.TestApiKeyAsync(ct)));
        if (!string.IsNullOrWhiteSpace(ext.IgdbClientId) && !string.IsNullOrWhiteSpace(ext.IgdbClientSecret) && ext.IgdbEnabled != false)
            tasks.Add(("igdb", () => _igdb.TestCredsAsync(ct)));

        var taskResults = tasks.Select(async t =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var ok = await t.test();
                sw.Stop();
                return new { provider = t.provider, ok, elapsedMs = sw.ElapsedMilliseconds, error = (string?)null };
            }
            catch (Exception ex)
            {
                sw.Stop();
                var safeError = ErrorMessageSanitizer.ToOperationalMessage(ex, "provider test failed");
                return new { provider = t.provider, ok = false, elapsedMs = sw.ElapsedMilliseconds, error = (string?)safeError };
            }
        }).ToList();

        var all = await Task.WhenAll(taskResults);

        return Ok(new { ok = true, results = all });
    }

    // POST /api/maintenance/reparse-titles
    [HttpPost("reparse-titles")]
    public IActionResult ReparseTitles()
    {
        try
        {
            using var conn = _db.Open();
            var total = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM releases;");
            if (total == 0)
                return Ok(new { ok = true, total = 0, updated = 0 });

            var updated = _releases.ReparseAllTitles();

            _activity.Add(null, "info", "maintenance", "Titles re-parsed",
                dataJson: $"{{\"total\":{total},\"updated\":{updated}}}");

            return Ok(new { ok = true, total, updated });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Reparse titles maintenance task failed");
            return StatusCode(500, new { error = "internal server error" });
        }
    }

    // POST /api/maintenance/detect-duplicates
    [HttpPost("detect-duplicates")]
    public IActionResult DetectDuplicates([FromQuery] bool purge = false)
    {
        try
        {
            var result = _releases.DetectDuplicates();

            if (!purge)
                return Ok(new { ok = true, groupsFound = result.GroupsFound, duplicatesCount = result.DuplicatesCount, purged = false });

            var deleted = _releases.PurgeDuplicates(result.DuplicateIds);

            _activity.Add(null, "info", "maintenance", "Duplicates purged",
                dataJson: $"{{\"groupsFound\":{result.GroupsFound},\"duplicatesCount\":{result.DuplicatesCount},\"deleted\":{deleted}}}");

            return Ok(new { ok = true, groupsFound = result.GroupsFound, duplicatesCount = result.DuplicatesCount, purged = true, deleted });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Duplicate detection maintenance task failed");
            return StatusCode(500, new { error = "internal server error" });
        }
    }
}
