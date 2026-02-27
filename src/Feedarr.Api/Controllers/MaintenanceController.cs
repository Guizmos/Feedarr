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
using Feedarr.Api.Services.Categories;
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
    private readonly MaintenanceLockService _maintenanceLock;
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
        MaintenanceLockService maintenanceLock,
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
        _maintenanceLock = maintenanceLock;
        _log = log;
    }

    // POST /api/maintenance/vacuum
    [HttpPost("vacuum")]
    public IActionResult Vacuum()
    {
        if (!_maintenanceLock.TryEnter())
        {
            _log.LogWarning("Vacuum rejected – a maintenance operation is already running");
            return Conflict(new { error = "a maintenance operation is already running" });
        }
        try
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
        finally
        {
            _maintenanceLock.Release();
        }
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

    // GET /api/maintenance/audit-indexer-category-selection
    [HttpGet("audit-indexer-category-selection")]
    public IActionResult AuditIndexerCategorySelection()
    {
        using var conn = _db.Open();

        var onboardingDone = _settings.GetUi(new UiSettings()).OnboardingDone;

        var sourceRows = conn.Query<AuditSourceRow>(
            """
            SELECT
              id AS SourceId,
              name AS Name,
              enabled AS Enabled,
              torznab_url AS TorznabUrl
            FROM sources
            ORDER BY id ASC;
            """
        ).ToList();

        var selectedRows = conn.Query<AuditSelectedRow>(
            """
            SELECT
              source_id AS SourceId,
              cat_id AS CatId
            FROM source_selected_categories
            ORDER BY source_id ASC, cat_id ASC;
            """
        ).ToList();

        var mappingRows = conn.Query<AuditMappingRow>(
            """
            SELECT
              source_id AS SourceId,
              cat_id AS CatId,
              group_key AS GroupKey,
              group_label AS GroupLabel
            FROM source_category_mappings
            ORDER BY source_id ASC, cat_id ASC;
            """
        ).ToList();

        var latestFallbackRows = conn.Query<AuditFallbackRow>(
            """
            WITH ranked AS (
              SELECT
                source_id AS SourceId,
                created_at_ts AS CreatedAtTs,
                message AS Message,
                ROW_NUMBER() OVER (
                  PARTITION BY source_id
                  ORDER BY created_at_ts DESC, id DESC
                ) AS rn
              FROM activity_log
              WHERE source_id IS NOT NULL
                AND event_type = 'sync'
                AND (
                  message LIKE '%Sync category-map:%'
                  OR message LIKE '%AutoSync category-map:%'
                )
            )
            SELECT SourceId, CreatedAtTs, Message
            FROM ranked
            WHERE rn = 1;
            """
        ).ToDictionary(row => row.SourceId, row => row);

        var mappingsBySource = mappingRows
            .GroupBy(row => row.SourceId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var selectedBySource = selectedRows
            .GroupBy(row => row.SourceId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var indexers = sourceRows.Select(source =>
        {
            var selected = selectedBySource.TryGetValue(source.SourceId, out var selectedList)
                ? selectedList
                : new List<AuditSelectedRow>();
            var mapping = mappingsBySource.TryGetValue(source.SourceId, out var list)
                ? list
                : new List<AuditMappingRow>();

            var persistedIds = selected
                .Select(row => row.CatId)
                .Where(catId => catId > 0)
                .Distinct()
                .OrderBy(catId => catId)
                .ToList();

            var persistedRaw = persistedIds.Count > 0
                ? string.Join(",", persistedIds)
                : null;

            var parsedPersisted = CategorySelectionAudit.ParseRawIds(persistedRaw, out var parseErrorCount)
                .ToList();

            var mappedIds = mapping
                .Where(row => !string.IsNullOrWhiteSpace(row.GroupKey))
                .Select(row => row.CatId)
                .Where(catId => catId > 0)
                .Distinct()
                .OrderBy(catId => catId)
                .ToList();

            var mappingOnlyIds = mappedIds
                .Except(parsedPersisted)
                .OrderBy(catId => catId)
                .ToList();

            var effectiveSelection = CategorySelection.NormalizeSelectedCategoryIds(parsedPersisted)
                .OrderBy(catId => catId)
                .ToList();

            var reason = CategorySelectionAudit.InferReason(
                persistedWasNull: persistedRaw is null,
                persistedCount: parsedPersisted.Count,
                parseErrorCount: parseErrorCount,
                mappedCount: parsedPersisted.Count,
                wizardIncomplete: !onboardingDone && parsedPersisted.Count == 0);

            var fallbackCount = 0;
            var noMapMatchCount = 0;
            string? fallbackMessage = null;
            long? fallbackAtTs = null;

            if (latestFallbackRows.TryGetValue(source.SourceId, out var fallbackRow))
            {
                fallbackMessage = fallbackRow.Message;
                fallbackAtTs = fallbackRow.CreatedAtTs;
                fallbackCount = ExtractMetricValue(fallbackRow.Message, "fallbackSelectedCategoryCount");
                noMapMatchCount = ExtractMetricValue(fallbackRow.Message, "noMapMatchCount");
            }

            return new
            {
                sourceId = source.SourceId,
                name = source.Name,
                enabled = source.Enabled == 1,
                torznabUrl = source.TorznabUrl,
                selectionSource = "source_selected_categories.cat_id",
                persistedSelection = new
                {
                    raw = persistedRaw,
                    parsed = parsedPersisted,
                    count = parsedPersisted.Count,
                    isNull = persistedRaw is null,
                    isEmpty = parsedPersisted.Count == 0,
                    parseErrorCount,
                    contains = new
                    {
                        id7000 = parsedPersisted.Contains(7000),
                        id107000 = parsedPersisted.Contains(107000),
                        id3000 = parsedPersisted.Contains(3000),
                        id103000 = parsedPersisted.Contains(103000),
                        id5070 = parsedPersisted.Contains(5070)
                    }
                },
                mappingStats = new
                {
                    source = "source_category_mappings",
                    mappedCatIds = mappedIds,
                    mappedCount = mappedIds.Count,
                    mappedNotSelectedCatIds = mappingOnlyIds,
                    mappedNotSelectedCount = mappingOnlyIds.Count
                },
                effectiveSelection = new
                {
                    ids = effectiveSelection,
                    count = effectiveSelection.Count,
                    sampleIds = effectiveSelection.Take(10).ToList()
                },
                usedFallback = CategorySelectionAudit.ShouldUseFallback(effectiveSelection),
                fallbackCount,
                noMapMatchCount,
                reason,
                lastCategoryMapEvidence = fallbackMessage is null
                    ? null
                    : new
                    {
                        createdAtTs = fallbackAtTs,
                        createdAtIso = fallbackAtTs.HasValue
                            ? DateTimeOffset.FromUnixTimeSeconds(fallbackAtTs.Value).ToString("O")
                            : null,
                        message = fallbackMessage
                    }
            };
        }).ToList();

        return Ok(new
        {
            generatedAtTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            generatedAtIso = DateTimeOffset.UtcNow.ToString("O"),
            onboardingDone,
            indexers
        });
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
        if (!_maintenanceLock.TryEnter())
        {
            _log.LogWarning("CleanupPosters rejected – a maintenance operation is already running");
            return Conflict(new { error = "a maintenance operation is already running" });
        }

        try
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

            // Pre-compute canonical root path with trailing separator for safe containment check
            var canonicalRoot = Path.GetFullPath(postersDir);
            if (!canonicalRoot.EndsWith(Path.DirectorySeparatorChar))
                canonicalRoot += Path.DirectorySeparatorChar;

            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                if (referencedPosters.Contains(fileName))
                    continue;

                // Validate file extension is an image type.
                // Parentheses around the && chain are required: without them the ||
                // binds tighter than && and the condition logic is incorrect.
                var ext = Path.GetExtension(fileName);
                if (string.IsNullOrEmpty(ext) ||
                    (!ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
                     !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) &&
                     !ext.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
                     !ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)))
                    continue;

                orphaned++;
                try
                {
                    var full = Path.GetFullPath(file);
                    if (!full.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var size = new FileInfo(file).Length;

                    // DB-first: clear the reference before deleting the file.
                    // If the DB write fails we abort without touching the file,
                    // so no broken reference pointing to a missing file can arise.
                    // If the subsequent file delete fails, the DB reference is
                    // already gone; the file will be cleaned up on the next run.
                    _releases.ClearPosterFileReferences(fileName);

                    System.IO.File.Delete(file);
                    if (System.IO.File.Exists(file))
                    {
                        _log.LogWarning("Poster file still exists after delete attempt for {File}", fileName);
                        continue;
                    }

                    freedBytes += size;
                    deleted++;
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
        finally
        {
            _maintenanceLock.Release();
        }
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
        if (!_maintenanceLock.TryEnter())
        {
            _log.LogWarning("DetectDuplicates rejected – a maintenance operation is already running");
            return Conflict(new { error = "a maintenance operation is already running" });
        }
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
        finally
        {
            _maintenanceLock.Release();
        }
    }

    // POST /api/maintenance/reprocess-categories
    [HttpPost("reprocess-categories")]
    public IActionResult ReprocessCategories()
    {
        if (!_maintenanceLock.TryEnter())
        {
            _log.LogWarning("ReprocessCategories rejected – a maintenance operation is already running");
            return Conflict(new { error = "a maintenance operation is already running" });
        }
        try
        {
            var (processed, updated, markedRebind) = _releases.ReprocessCategories();

            _activity.Add(null, "info", "maintenance", "Categories re-processed",
                dataJson: $"{{\"processed\":{processed},\"updated\":{updated},\"markedRebind\":{markedRebind}}}");

            return Ok(new { ok = true, processed, updated, markedRebind });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Reprocess categories maintenance task failed");
            return StatusCode(500, new { error = "internal server error" });
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    // POST /api/maintenance/rebind-entities?batchSize=200
    // À appeler après reprocess-categories pour corriger entity_id des releases recatégorisées.
    // Traite les releases WHERE needs_rebind=1 par batch (curseur id) et recalcule entity_id.
    [HttpPost("rebind-entities")]
    public IActionResult RebindEntities([FromQuery] int batchSize = 200)
    {
        if (!_maintenanceLock.TryEnter())
        {
            _log.LogWarning("RebindEntities rejected – a maintenance operation is already running");
            return Conflict(new { error = "a maintenance operation is already running" });
        }
        try
        {
            var (processed, rebound) = _releases.RebindEntities(batchSize);

            _activity.Add(null, "info", "maintenance", "Entity rebind completed",
                dataJson: $"{{\"processed\":{processed},\"rebound\":{rebound}}}");

            return Ok(new { ok = true, processed, rebound });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Rebind entities maintenance task failed");
            return StatusCode(500, new { error = "internal server error" });
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    private static int ExtractMetricValue(string? message, string metricName)
    {
        if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(metricName))
            return 0;

        var token = metricName + "=";
        var start = message.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return 0;

        start += token.Length;
        var end = start;
        while (end < message.Length && char.IsDigit(message[end]))
            end++;

        if (end <= start)
            return 0;

        return int.TryParse(message.AsSpan(start, end - start), out var parsed) ? parsed : 0;
    }

    private sealed class AuditSourceRow
    {
        public long SourceId { get; set; }
        public string Name { get; set; } = "";
        public int Enabled { get; set; }
        public string TorznabUrl { get; set; } = "";
    }

    private sealed class AuditMappingRow
    {
        public long SourceId { get; set; }
        public int CatId { get; set; }
        public string? GroupKey { get; set; }
        public string? GroupLabel { get; set; }
    }

    private sealed class AuditSelectedRow
    {
        public long SourceId { get; set; }
        public int CatId { get; set; }
    }

    private sealed class AuditFallbackRow
    {
        public long SourceId { get; set; }
        public long CreatedAtTs { get; set; }
        public string Message { get; set; } = "";
    }
}
