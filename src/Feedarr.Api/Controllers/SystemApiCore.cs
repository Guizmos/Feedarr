using Microsoft.AspNetCore.Mvc;
using Dapper;
using System.Diagnostics;
using System.Reflection;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Dtos.System;
using Feedarr.Api.Helpers;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Diagnostics;
using Feedarr.Api.Services.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.RateLimiting;
using System.Data;
using Feedarr.Api.Services.Updates;

namespace Feedarr.Api.Controllers;

public sealed partial class SystemApiCore : ControllerBase
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

    // Cursor tokens for keyset pagination (base64url-encoded JSON, opaque to clients)
    private sealed record ProviderCursor(long MatchedCount, string ProviderKey);
    private sealed record IndexerCategoryCursor(
        string SourceName, int Count, long SourceId, string UnifiedCategory);

    // Captures the real start-up instant once, on first type load — readonly, no mutation.
    private static readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private static readonly TimeSpan StatsCacheDuration = TimeSpan.FromSeconds(20);

    private readonly Db _db;
    private readonly IWebHostEnvironment _env;
    private readonly SettingsRepository _settings;
    private readonly ProviderStatsService _providerStats;
    private readonly ApiRequestMetricsService _apiRequestMetrics;
    private readonly BackupService _backupService;
    private readonly SystemStatusCacheService _systemStatusCache;
    private readonly IMemoryCache _cache;
    private readonly SetupStateService _setupState;
    private readonly StorageUsageCacheService _storageCache;
    private readonly ILogger<SystemApiCore> _log;

    public SystemApiCore(
        Db db,
        IWebHostEnvironment env,
        SettingsRepository settings,
        ProviderStatsService providerStats,
        ApiRequestMetricsService apiRequestMetrics,
        BackupService backupService,
        SystemStatusCacheService systemStatusCache,
        IMemoryCache cache,
        SetupStateService setupState,
        StorageUsageCacheService storageCache,
        ILogger<SystemApiCore> log)
    {
        _db = db;
        _env = env;
        _settings = settings;
        _providerStats = providerStats;
        _apiRequestMetrics = apiRequestMetrics;
        _backupService = backupService;
        _systemStatusCache = systemStatusCache;
        _cache = cache;
        _setupState = setupState;
        _storageCache = storageCache;
        _log = log;
    }

    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Status([FromQuery] long? releasesSinceTs = null, CancellationToken ct = default)
    {
        var safeSinceTs = Math.Max(0L, releasesSinceTs ?? 0L);
        var snapshot = await _systemStatusCache.GetSnapshotAsync(ct).ConfigureAwait(false);
        int? releasesNewSinceTsCount = null;

        if (safeSinceTs > 0)
        {
            using var conn = _db.Open();
            releasesNewSinceTsCount = conn.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM releases WHERE created_at_ts > @sinceTs;",
                new { sinceTs = safeSinceTs });
        }

        var version = GetAppVersion();

        var dto = new SystemStatusDto
        {
            Version = version,
            Environment = _env.EnvironmentName,
            UptimeSeconds = (long)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
            DataDir = "hidden",
            DbSizeMB = snapshot.DbSizeMb,
            SourcesCount = snapshot.SourcesCount,
            ReleasesCount = snapshot.ReleasesCount,
            ReleasesLatestTs = snapshot.ReleasesLatestTs,
            ReleasesNewSinceTsCount = releasesNewSinceTsCount,
            LastSyncAtTs = snapshot.LastSyncAtTs
        };

        return Ok(dto);
    }

    // GET /api/system/providers
    [HttpGet("providers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Providers()
    {
        var stats = _providerStats.SnapshotByProvider();
        return Ok(ToProviderStatsPayload(stats));
    }

    // GET /api/system/perf
    [HttpGet("perf")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Performance([FromQuery] int top = 20)
    {
        return Ok(_apiRequestMetrics.Snapshot(top));
    }

    // GET /api/system/onboarding
    [HttpGet("onboarding")]
    [ProducesResponseType(StatusCodes.Status200OK)]
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

        var onboardingDone = _setupState.IsSetupCompleted();

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
        _setupState.MarkSetupCompleted();
        return Ok(new { ok = true, onboardingDone = true });
    }

    // POST /api/system/onboarding/reset
    [HttpPost("onboarding/reset")]
    public IActionResult ResetOnboarding()
    {
        _setupState.ResetSetupCompleted();
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
    public async Task<IActionResult> PurgeBackups(CancellationToken ct)
    {
        var deleted = await _backupService.PurgeBackupsAsync(ct);
        return Ok(new { ok = true, deleted });
    }

    // POST /api/system/backups
    [HttpPost("backups")]
    public async Task<IActionResult> CreateBackup(CancellationToken ct)
    {
        try
        {
            var backup = await _backupService.CreateBackupAsync(GetAppVersion(), ct);
            return Ok(backup);
        }
        catch (BackupOperationException ex)
        {
            return ToBackupErrorResult(ex);
        }
    }

    // DELETE /api/system/backups/{name}
    [HttpDelete("backups/{name}")]
    public async Task<IActionResult> DeleteBackup([FromRoute] string name, CancellationToken ct)
    {
        try
        {
            await _backupService.DeleteBackupAsync(name, ct);
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

    // POST /api/system/backups/{name}/restore?confirm=false
    //
    // confirm=false (default): dry-run preview — validates the archive and reports which credentials
    //   would be re-encrypted or cleared, WITHOUT touching the live database.
    //   Response: { dryRun: true, wouldReencrypt: N, wouldClear: N }
    //
    // confirm=true: performs the actual restore. If wouldClear > 0, the caller MUST have passed
    //   confirm=true explicitly, acknowledging that some credentials will be erased.
    //   Response: { ok: true, needsRestart: true, reencryptedCredentials: N, clearedUndecryptableCredentials: N }
    [HttpPost("backups/{name}/restore")]
    public async Task<IActionResult> RestoreBackup([FromRoute] string name, [FromQuery] bool confirm = false, CancellationToken ct = default)
    {
        try
        {
            if (!confirm)
            {
                // Dry-run: return a preview without modifying anything.
                var preview = await _backupService.PreviewRestoreBackupAsync(name, GetAppVersion(), ct);

                var previewWarning = preview.WouldClear > 0
                    ? $"{preview.WouldClear} credential(s) could not be decrypted with the current key ring " +
                      "and would be permanently cleared. Pass confirm=true to proceed."
                    : null;

                return Ok(new
                {
                    dryRun = true,
                    wouldReencrypt = preview.WouldReencrypt,
                    wouldClear = preview.WouldClear,
                    warning = previewWarning
                });
            }

            // Actual restore.
            var result = await _backupService.RestoreBackupAsync(name, GetAppVersion(), ct);

            var warning = result.ClearedUndecryptableCredentials > 0
                ? $"{result.ClearedUndecryptableCredentials} credential(s) could not be decrypted " +
                  "and were cleared. Reconfigure them in Settings → External Providers."
                : null;

            if (result.ClearedUndecryptableCredentials > 0)
                _log.LogWarning(
                    "RestoreBackup {Name}: {Cleared} credential(s) cleared – key ring mismatch",
                    name, result.ClearedUndecryptableCredentials);

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

    // GET /api/system/storage
    [HttpGet("storage")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Storage(CancellationToken ct)
    {
        var volumes = new List<DiskVolumeDto>();
        var isLinux = !OperatingSystem.IsWindows();

        if (isLinux)
        {
            // On Linux/Docker, show only relevant paths (DataDir and common mount points)
            var pathsToCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                _storageCache.DataDirAbs,
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

        var storage = await _storageCache.GetSnapshotAsync(ct);

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

        if (TryResolveVersion(envVersion, out var parsedEnv))
            return parsedEnv;
        if (TryResolveVersion(infoVersion, out var parsedInfo))
            return parsedInfo;
        if (TryResolveVersion(asmVersion, out var parsedAsm))
            return parsedAsm;

        return "0.0.0";
    }

    private static bool TryResolveVersion(string? value, out string version)
    {
        version = "";
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (ReleaseVersionComparer.TryParse(trimmed, out var parsed))
        {
            version = ReleaseVersionComparer.ToCanonicalString(parsed);
            return true;
        }

        if (IsBetaTrackVersion(trimmed))
        {
            version = trimmed;
            return true;
        }

        return false;
    }

    private static bool IsBetaTrackVersion(string? value)
        => !string.IsNullOrWhiteSpace(value)
        && value.Trim().StartsWith("beta-", StringComparison.OrdinalIgnoreCase);

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
}
