using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Dtos.System;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Feedarr.Api.Services.Backup;

public sealed class BackupService : IHostedService
{
    private static readonly JsonSerializerOptions BackupJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private const long MaxExtractedDatabaseBytes = 2L * 1024 * 1024 * 1024; // 2 GB
    private const string RestartRequiredErrorMessage = "Redemarrage requis apres restauration. Action impossible avant redemarrage.";

    private readonly Db _db;
    private readonly IWebHostEnvironment _env;
    private readonly AppOptions _opts;
    private readonly SettingsRepository _settings;
    private readonly BackupValidationService _validation;
    private readonly BackupExecutionCoordinator _coordinator;
    private readonly IApiKeyProtectionService _apiKeyProtection;
    private readonly ILogger<BackupService> _logger;
    private readonly PendingRestoreStore _pending;

    private readonly object _preparationLock = new();
    private readonly CancellationTokenSource _preparationStopping = new();
    private Task? _preparationTask;
    private RestorePreparationStatus? _preparation;
    internal TimeSpan PreparationTimeout { get; set; } = TimeSpan.FromHours(2);
    internal TimeProvider PreparationClock { get; set; } = TimeProvider.System;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken ct)
    {
        _preparationStopping.Cancel();
        Task? running;
        lock (_preparationLock) running = _preparationTask;
        if (running is not null) await running.WaitAsync(ct).ConfigureAwait(false);
    }

    public RestorePreparationStatus StartRestorePreparation(string name, string version, string? replacePendingId = null)
    {
        lock (_preparationLock)
        {
            EnsureRestartNotRequired();
            if (_preparationStopping.IsCancellationRequested)
                throw new BackupOperationException("Le serveur est en cours d’arrêt ; réessayez après son redémarrage.", 503);
            if (_preparationTask is { IsCompleted: false } || _coordinator.GetState().IsBusy)
                throw new BackupOperationException("Une opération de sauvegarde ou de restauration est déjà en cours.", 409);
            _pending.EnsureCanPrepare(replacePendingId);
            GetExistingBackupFile(name);
            var id = Guid.NewGuid().ToString("N");
            var accepted = new RestorePreparationStatus(id, "running");
            _preparation = accepted;
            _preparationTask = Task.Run(async () =>
            {
                var clock = PreparationClock;
                var budget = PreparationTimeout;
                var preparationStarted = clock.GetTimestamp();
                using var deadline = new CancellationTokenSource(budget, clock);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_preparationStopping.Token, deadline.Token);
                // Timer cancellation is cooperative and its callback may be delayed
                // by thread-pool load. Recheck monotonic time at publication boundaries.
                void EnsurePreparationDeadline()
                {
                    if (clock.GetElapsedTime(preparationStarted) >= budget) timeout.Cancel();
                    timeout.Token.ThrowIfCancellationRequested();
                }
                var started = System.Diagnostics.Stopwatch.StartNew();
                _logger.LogInformation("Restore preparation {PreparationId} started independently of HTTP", id);
                try
                {
                    var result = await RestoreBackupAsync(name, version, timeout.Token, replacePendingId, id, EnsurePreparationDeadline).ConfigureAwait(false);
                    var pending = _pending.GetStatus();
                    if (!pending.Pending || pending.State != "pending" || pending.Id != id)
                        throw new BackupOperationException("Le manifeste de restauration préparée n'a pas pu être confirmé.");
                    lock (_preparationLock) _preparation = new(id, "completed", Result: result);
                    _logger.LogInformation("Restore preparation {PreparationId} published after {ElapsedMs}ms", id, started.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    var error = ex is OperationCanceledException
                        ? (_preparationStopping.IsCancellationRequested ? "Préparation interrompue par l'arrêt du serveur." : "Délai maximal de préparation dépassé (2 heures).")
                        : ErrorMessageSanitizer.ToOperationalMessage(ex, "Échec de préparation ; consultez les journaux du serveur.");
                    lock (_preparationLock) _preparation = new(id, "failed", Error: error);
                    _logger.LogError(ex, "Restore preparation {PreparationId} failed after {ElapsedMs}ms", id, started.ElapsedMilliseconds);
                }
            });
            return accepted;
        }
    }

    private string DataDirAbs =>
        Path.IsPathRooted(_opts.DataDir)
            ? _opts.DataDir
            : Path.GetFullPath(Path.Combine(_env.ContentRootPath, _opts.DataDir));

    private string BackupDirAbs => Path.Combine(DataDirAbs, "backups");
    private string DbPathAbs => Path.Combine(DataDirAbs, _opts.DbFileName);
    private string RestartRequiredFlagPath => Path.Combine(DataDirAbs, "restore.restart-required.flag");

    public BackupService(
        Db db,
        IWebHostEnvironment env,
        Microsoft.Extensions.Options.IOptions<AppOptions> opts,
        SettingsRepository settings,
        BackupValidationService validation,
        BackupExecutionCoordinator coordinator,
        IApiKeyProtectionService apiKeyProtection,
        ILogger<BackupService> logger,
        PendingRestoreStore? pending = null)
    {
        _db = db;
        _env = env;
        _opts = opts.Value;
        _settings = settings;
        _validation = validation;
        _coordinator = coordinator;
        _apiKeyProtection = apiKeyProtection;
        _logger = logger;
        _pending = pending ?? new PendingRestoreStore(DataDirAbs, _opts.DbFileName);
    }

    public IReadOnlyList<BackupFileDto> ListBackups()
    {
        Directory.CreateDirectory(BackupDirAbs);
        return new DirectoryInfo(BackupDirAbs)
            .EnumerateFiles("*.zip", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new BackupFileDto
            {
                Name = f.Name,
                SizeBytes = f.Length,
                CreatedAtTs = new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeSeconds()
            })
            .ToList();
    }

    private void EnsurePreparationNotRunning()
    {
        lock (_preparationLock)
            if (_preparation?.State == "running")
                throw new BackupOperationException("Une préparation de restauration est déjà en cours.", 409);
    }

    public Task<int> PurgeBackupsAsync(CancellationToken ct = default)
    {
        EnsurePreparationNotRunning();
        EnsureRestartNotRequired();
        return _coordinator.RunExclusiveAsync("purge", null, async opCt =>
        {
            Directory.CreateDirectory(BackupDirAbs);
            var files = new DirectoryInfo(BackupDirAbs)
                .EnumerateFiles("*.zip", SearchOption.TopDirectoryOnly)
                .ToList();

            var deleted = 0;
            foreach (var file in files)
            {
                opCt.ThrowIfCancellationRequested();
                var path = file.FullName;
                TryDeleteFile(path);
                if (!System.IO.File.Exists(path))
                    deleted++;
            }

            return await Task.FromResult(deleted).ConfigureAwait(false);
        }, ct);
    }

    public Task<BackupFileDto> CreateBackupAsync(string appVersion, CancellationToken ct = default)
    {
        EnsurePreparationNotRunning();
        EnsureRestartNotRequired();
        return _coordinator.RunExclusiveAsync("create", null, async opCt =>
        {
            opCt.ThrowIfCancellationRequested();
            if (!System.IO.File.Exists(DbPathAbs))
                throw new BackupOperationException("database not found", StatusCodes.Status404NotFound);

            Directory.CreateDirectory(BackupDirAbs);

            var version = SanitizeFileFragment(appVersion);
            if (string.IsNullOrWhiteSpace(version)) version = "0.0.0";

            var timestamp = DateTime.UtcNow;
            var baseName = $"feedarr_backup_v{version}_{timestamp:yyyy.MM.dd_HH.mm.ss}";
            var zipPath = Path.Combine(BackupDirAbs, $"{baseName}.zip");
            var tempDbPath = Path.Combine(BackupDirAbs, $"{baseName}.db");

            if (System.IO.File.Exists(zipPath))
            {
                baseName = $"{baseName}_{Guid.NewGuid():N}";
                zipPath = Path.Combine(BackupDirAbs, $"{baseName}.zip");
                tempDbPath = Path.Combine(BackupDirAbs, $"{baseName}.db");
            }

            try
            {
                CreateDbSnapshot(tempDbPath);
                var dbSha256 = ComputeFileSha256(tempDbPath);
                var dbSizeBytes = new FileInfo(tempDbPath).Length;

                using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                archive.CreateEntryFromFile(tempDbPath, _opts.DbFileName);
                AddJsonEntry(archive, "config.json", BuildBackupConfig());
                AddJsonEntry(archive, "info.json", BuildBackupInfo(timestamp, appVersion, "manual", dbSha256, dbSizeBytes));
            }
            catch (Exception ex)
            {
                TryDeleteFile(zipPath);
                throw new BackupOperationException(
                    ErrorMessageSanitizer.ToOperationalMessage(ex, "backup creation failed"),
                    ex);
            }
            finally
            {
                TryDeleteFile(tempDbPath);
            }

            var info = new FileInfo(zipPath);
            return await Task.FromResult(new BackupFileDto
            {
                Name = info.Name,
                SizeBytes = info.Length,
                CreatedAtTs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds()
            });
        }, ct);
    }

    public (string SafeName, string FullPath) GetExistingBackupFile(string name)
    {
        EnsureRestartNotRequired();
        var safeName = SanitizeBackupName(name);
        if (string.IsNullOrWhiteSpace(safeName))
            throw new BackupOperationException("invalid backup name", StatusCodes.Status400BadRequest);

        var path = Path.Combine(BackupDirAbs, safeName);
        if (!System.IO.File.Exists(path))
            throw new BackupOperationException("backup not found", StatusCodes.Status404NotFound);

        return (safeName, path);
    }

    public async Task DeleteBackupAsync(string name, CancellationToken ct = default)
    {
        EnsurePreparationNotRunning();
        await _coordinator.RunExclusiveAsync("delete", name, opCt =>
        {
            opCt.ThrowIfCancellationRequested();
            var (_, path) = GetExistingBackupFile(name);
            TryDeleteFile(path);
            if (System.IO.File.Exists(path))
                throw new BackupOperationException("backup delete failed");

            return Task.FromResult(true);
        }, ct);
    }

    /// <summary>
    /// Dry-run: validates the backup archive and analyses which credentials would be re-encrypted
    /// or cleared, WITHOUT applying any changes to the live database.
    /// Safe to call without <see cref="confirm"/> parameter.
    /// </summary>
    public async Task<BackupRestorePreview> PreviewRestoreBackupAsync(
        string name,
        string currentAppVersion,
        CancellationToken ct = default)
    {
        EnsureRestartNotRequired();

        // Preview is read-only on a temp extract — no coordinator lock needed.
        var (_, backupPath) = GetExistingBackupFile(name);
        var operationId = Guid.NewGuid().ToString("N");
        var workDir = Path.Combine(BackupDirAbs, $"preview-{operationId}");
        var extractedDbPath = Path.Combine(workDir, "preview.db");

        Directory.CreateDirectory(workDir);

        try
        {
            ct.ThrowIfCancellationRequested();
            using var archive = ZipFile.OpenRead(backupPath);
            if (!_validation.TryValidateArchiveForRestore(
                    archive,
                    _opts.DbFileName,
                    currentAppVersion,
                    out var dbEntryName,
                    out _,
                    out var validationError))
            {
                throw new BackupOperationException(validationError, StatusCodes.Status400BadRequest);
            }

            var dbEntry = archive.GetEntry(dbEntryName);
            if (dbEntry is null)
                throw new BackupOperationException("backup database missing", StatusCodes.Status400BadRequest);

            ct.ThrowIfCancellationRequested();
            ExtractEntryControlled(dbEntry, extractedDbPath);
            VerifySqliteIntegrity(extractedDbPath);

            ct.ThrowIfCancellationRequested();
            var (wouldReencrypt, wouldClear) = AnalyzeRestoredCredentials(extractedDbPath);

            _logger.LogInformation(
                "Backup preview for {Name}: wouldReencrypt={Re} wouldClear={Clear}",
                name, wouldReencrypt, wouldClear);

            return await Task.FromResult(new BackupRestorePreview
            {
                WouldReencrypt = wouldReencrypt,
                WouldClear = wouldClear
            });
        }
        catch (BackupOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backup preview failed for {BackupName}", name);
            throw new BackupOperationException(
                ErrorMessageSanitizer.ToOperationalMessage(ex, "backup preview failed"),
                ex);
        }
        finally
        {
            TryDeleteFile(extractedDbPath);
            TryDeleteDirectory(workDir);
        }
    }

    public Task<BackupRestoreResult> RestoreBackupAsync(
        string name, string currentAppVersion, CancellationToken ct = default, string? replacePendingId = null, string? preparationId = null)
        => RestoreBackupAsync(name, currentAppVersion, ct, replacePendingId, preparationId, null);

    private Task<BackupRestoreResult> RestoreBackupAsync(
        string name, string currentAppVersion, CancellationToken ct, string? replacePendingId, string? preparationId, Action? ensurePreparationDeadline)
    {
        EnsureRestartNotRequired();
        return _coordinator.RunExclusiveAsync("prepare-restore", name, async opCt =>
        {
            _pending.EnsureCanPrepare(replacePendingId);
            var (_, backupPath) = GetExistingBackupFile(name);
            var work = _pending.NewWorkDirectory();
            var extracted = Path.Combine(work, "incoming.db");
            try
            {
                opCt.ThrowIfCancellationRequested();
                using var archive = ZipFile.OpenRead(backupPath);
                if (!_validation.TryValidateArchiveForRestore(archive, _opts.DbFileName, currentAppVersion,
                    out var entryName, out var details, out var error))
                    throw new BackupOperationException(error, StatusCodes.Status400BadRequest);
                ExtractEntryControlled(archive.GetEntry(entryName)!, extracted);
                if (!string.Equals(ComputeFileSha256(extracted), details.DbSha256, StringComparison.OrdinalIgnoreCase))
                    throw new BackupOperationException("backup database checksum mismatch", StatusCodes.Status400BadRequest);
                VerifySqliteIntegrity(extracted);
                var credentials = NormalizeRestoredCredentials(extracted);
                VerifySqliteIntegrity(extracted);
                var status = _pending.Publish(work, name, replacePendingId, opCt, preparationId, ensurePreparationDeadline);
                return await Task.FromResult(new BackupRestoreResult
                {
                    ReencryptedCredentials = credentials.Reencrypted,
                    ClearedUndecryptableCredentials = credentials.ClearedUndecryptable,
                    MissingPosters = status.MissingPosters
                });
            }
            catch (OperationCanceledException) { throw; }
            catch (BackupOperationException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backup restore preparation failed for {BackupName}", name);
                throw new BackupOperationException("backup preparation failed; check server logs", ex);
            }
            finally { TryDeleteDirectory(work); } // published work has already moved to its persistent stage
        }, ct, blockSync: false);
    }

    public PendingRestoreStatus GetPendingRestore() => _pending.GetStatus();
    public void CancelPendingRestore(string id) => _pending.Cancel(id);

    public BackupOperationStateDto GetOperationState()
    {
        var state = _coordinator.GetState();
        lock (_preparationLock)
        {
            state.Preparation = _preparation;
            if (_preparation?.State == "running" && !state.IsBusy)
            {
                state.IsBusy = true;
                state.Operation = "prepare-restore";
                state.Phase = "running";
            }
        }
        state.RestorePending = _pending.GetStatus();
        state.NeedsRestart = IsRestartRequired() || state.RestorePending.Pending;
        return state;
    }

    public void InitializeForStartup()
    {
        Directory.CreateDirectory(DataDirAbs);
        TryDeleteFile(RestartRequiredFlagPath);
    }

    public bool IsRestartRequired() => System.IO.File.Exists(RestartRequiredFlagPath);

    private void EnsureRestartNotRequired()
    {
        if (IsRestartRequired())
            throw new BackupOperationException(RestartRequiredErrorMessage, StatusCodes.Status409Conflict);
    }

    private void CheckpointCurrentDatabase()
    {
        if (!System.IO.File.Exists(DbPathAbs))
            return;

        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = DbPathAbs,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString();

            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteScalar();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database WAL checkpoint failed, backup will use current DB state");
        }
    }

    private static void VerifySqliteIntegrity(string dbPath) => PendingRestoreStore.VerifyDatabase(dbPath);

    private static void ExtractEntryControlled(ZipArchiveEntry entry, string destinationPath)
    {
        if (entry.Length <= 0 || entry.Length > MaxExtractedDatabaseBytes)
            throw new BackupOperationException("backup database too large", StatusCodes.Status400BadRequest);

        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        using var input = entry.Open();
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;

            total += read;
            if (total > MaxExtractedDatabaseBytes)
                throw new BackupOperationException("backup database too large", StatusCodes.Status400BadRequest);

            output.Write(buffer, 0, read);
        }

        if (total <= 0)
            throw new BackupOperationException("backup database is empty", StatusCodes.Status400BadRequest);
    }

    private void CreateDbSnapshot(string destinationPath)
    {
        TryDeleteFile(destinationPath);
        try
        {
            // Build a dedicated connection to use parameterized VACUUM INTO
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = DbPathAbs,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();

            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "VACUUM INTO $path;";
            cmd.Parameters.AddWithValue("$path", destinationPath);
            cmd.ExecuteNonQuery();
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VACUUM INTO failed for snapshot, falling back to checkpoint+copy");
        }

        CheckpointCurrentDatabase();
        System.IO.File.Copy(DbPathAbs, destinationPath, true);
    }

    private object BuildBackupConfig()
    {
        var general = _settings.GetGeneral(new GeneralSettings());
        var ui = _settings.GetUi(new UiSettings());
        var security = _settings.GetSecurity(new SecuritySettings());
        var external = _settings.GetExternal(new ExternalSettings());

        var securityExport = new
        {
            authentication = security.Authentication,
            authenticationRequired = security.AuthenticationRequired,
            username = security.Username,
            hasPassword = !string.IsNullOrWhiteSpace(security.PasswordHash)
        };

        var externalExport = new
        {
            tmdbEnabled = external.TmdbEnabled,
            tvmazeEnabled = external.TvmazeEnabled,
            fanartEnabled = external.FanartEnabled,
            igdbEnabled = external.IgdbEnabled,
            hasTmdbApiKey = !string.IsNullOrWhiteSpace(external.TmdbApiKey),
            hasTvmazeApiKey = !string.IsNullOrWhiteSpace(external.TvmazeApiKey),
            hasFanartApiKey = !string.IsNullOrWhiteSpace(external.FanartApiKey),
            hasIgdbClientId = !string.IsNullOrWhiteSpace(external.IgdbClientId),
            hasIgdbClientSecret = !string.IsNullOrWhiteSpace(external.IgdbClientSecret)
        };

        return new
        {
            general,
            ui,
            security = securityExport,
            external = externalExport
        };
    }

    private object BuildBackupInfo(
        DateTime createdAtUtc,
        string appVersion,
        string backupKind,
        string dbSha256,
        long dbSizeBytes)
    {
        return new
        {
            app = "Feedarr",
            version = appVersion,
            environment = _env.EnvironmentName,
            createdAt = new DateTimeOffset(createdAtUtc).ToUnixTimeSeconds(),
            createdAtIso = createdAtUtc.ToString("O"),
            dataDir = _opts.DataDir,
            dbFileName = _opts.DbFileName,
            dbSizeBytes,
            dbSha256,
            backupFormatVersion = BackupValidationService.CurrentBackupFormatVersion,
            backupKind,
            credentials = new
            {
                configSecretsRedacted = true,
                undecryptableProtectedKeysOnRestore = "cleared"
            }
        };
    }

    private CredentialRestoreReport NormalizeRestoredCredentials(string dbPath)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString();

        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var tx = conn.BeginTransaction();

        var report = CredentialRestoreReport.Empty;
        NormalizeCredentialsColumn(conn, tx, "sources", "api_key", ref report);
        NormalizeCredentialsColumn(conn, tx, "providers", "api_key", ref report);           // legacy column name (pre-migration 0045)
        NormalizeCredentialsColumn(conn, tx, "providers", "api_key_encrypted", ref report); // current column name (post-migration 0045)
        NormalizeCredentialsColumn(conn, tx, "arr_applications", "api_key_encrypted", ref report);

        tx.Commit();
        return report;
    }

    private void NormalizeCredentialsColumn(
        SqliteConnection conn,
        SqliteTransaction tx,
        string table,
        string column,
        ref CredentialRestoreReport report)
    {
        if (!ColumnExists(conn, tx, table, column))
            return;

        var tableSql = QuoteIdentifier(table);
        var columnSql = QuoteIdentifier(column);

        var selectSql = $"""
            SELECT rowid AS rowId, {columnSql} AS value
            FROM {tableSql}
            WHERE {columnSql} IS NOT NULL
              AND TRIM({columnSql}) <> '';
            """;

        var rows = conn.Query<(long RowId, string Value)>(selectSql, transaction: tx).ToList();
        if (rows.Count == 0)
            return;

        var updateSql = $"UPDATE {tableSql} SET {columnSql} = @value WHERE rowid = @rowId;";
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Value))
                continue;

            var currentValue = row.Value;

            if (_apiKeyProtection.IsProtected(currentValue))
            {
                if (_apiKeyProtection.TryUnprotect(currentValue, out var plainText) && plainText is not null)
                {
                    var reprotected = _apiKeyProtection.Protect(plainText);
                    if (!string.Equals(reprotected, currentValue, StringComparison.Ordinal))
                    {
                        conn.Execute(updateSql, new { value = reprotected, rowId = row.RowId }, tx);
                        report = report with { Reencrypted = report.Reencrypted + 1 };
                    }

                    continue;
                }

                conn.Execute(updateSql, new { value = "", rowId = row.RowId }, tx);
                report = report with { ClearedUndecryptable = report.ClearedUndecryptable + 1 };
                continue;
            }

            var encrypted = _apiKeyProtection.Protect(currentValue);
            if (!string.Equals(encrypted, currentValue, StringComparison.Ordinal))
            {
                conn.Execute(updateSql, new { value = encrypted, rowId = row.RowId }, tx);
                report = report with { Reencrypted = report.Reencrypted + 1 };
            }
        }
    }

    private static bool ColumnExists(SqliteConnection conn, SqliteTransaction tx, string table, string column)
    {
        var tableExists = conn.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = @table;",
            new { table },
            tx) > 0;

        if (!tableExists)
            return false;

        var pragmaSql = $"PRAGMA table_info({QuoteIdentifier(table)});";
        var existingColumns = conn.Query<(int Cid, string Name, string Type, int NotNull, string? DefaultValue, int Pk)>(
            pragmaSql,
            transaction: tx);

        return existingColumns.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
    }

    private static string QuoteIdentifier(string value)
        => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string SanitizeFileFragment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_')
                sb.Append(c);
        }
        return sb.ToString();
    }

    private static string SanitizeBackupName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var fileName = Path.GetFileName(name.Trim());
        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return "";

        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "";

        return fileName;
    }

    private static void AddJsonEntry(ZipArchive archive, string name, object payload)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        var json = JsonSerializer.Serialize(payload, BackupJsonOptions);
        writer.Write(json);
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
        }
    }

    private readonly record struct CredentialRestoreReport(int Reencrypted, int ClearedUndecryptable)
    {
        public static CredentialRestoreReport Empty => new(0, 0);
    }

    /// <summary>
    /// Analyses credentials in an extracted (temporary) database without writing anything.
    /// Opens the DB in ReadWrite mode on the temp file, runs the same column inspection logic
    /// as <see cref="NormalizeRestoredCredentials"/> but rolls back the transaction — so the
    /// temp file is left unchanged and zero changes reach the live database.
    /// </summary>
    private (int WouldReencrypt, int WouldClear) AnalyzeRestoredCredentials(string dbPath)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();

        using var conn = new SqliteConnection(cs);
        conn.Open();

        // Use a transaction so we can inspect rows with the same logic as NormalizeCredentialsColumn,
        // but rollback at the end — no actual changes are committed.
        using var tx = conn.BeginTransaction();

        var wouldReencrypt = 0;
        var wouldClear = 0;

        AnalyzeCredentialsColumn(conn, tx, "sources", "api_key", ref wouldReencrypt, ref wouldClear);
        AnalyzeCredentialsColumn(conn, tx, "providers", "api_key", ref wouldReencrypt, ref wouldClear);           // legacy column name (pre-migration 0045)
        AnalyzeCredentialsColumn(conn, tx, "providers", "api_key_encrypted", ref wouldReencrypt, ref wouldClear); // current column name (post-migration 0045)
        AnalyzeCredentialsColumn(conn, tx, "arr_applications", "api_key_encrypted", ref wouldReencrypt, ref wouldClear);

        tx.Rollback(); // Nothing persisted — temp file remains unmodified.
        return (wouldReencrypt, wouldClear);
    }

    private void AnalyzeCredentialsColumn(
        SqliteConnection conn,
        SqliteTransaction tx,
        string table,
        string column,
        ref int wouldReencrypt,
        ref int wouldClear)
    {
        if (!ColumnExists(conn, tx, table, column))
            return;

        var tableSql = QuoteIdentifier(table);
        var columnSql = QuoteIdentifier(column);

        var selectSql = $"""
            SELECT {columnSql} AS value
            FROM {tableSql}
            WHERE {columnSql} IS NOT NULL
              AND TRIM({columnSql}) <> '';
            """;

        var values = conn.Query<string>(selectSql, transaction: tx).ToList();

        foreach (var currentValue in values)
        {
            if (string.IsNullOrWhiteSpace(currentValue))
                continue;

            if (_apiKeyProtection.IsProtected(currentValue))
            {
                if (_apiKeyProtection.TryUnprotect(currentValue, out var plainText) && plainText is not null)
                {
                    var reprotected = _apiKeyProtection.Protect(plainText);
                    if (!string.Equals(reprotected, currentValue, StringComparison.Ordinal))
                        wouldReencrypt++;
                }
                else
                {
                    wouldClear++;
                }
            }
            else
            {
                // Plain-text credential — would be re-encrypted during actual restore.
                wouldReencrypt++;
            }
        }
    }
}

/// <summary>Result of a dry-run backup restore preview.</summary>
public sealed class BackupRestorePreview
{
    /// <summary>Number of credentials that would be re-encrypted with the current key ring.</summary>
    public int WouldReencrypt { get; init; }

    /// <summary>
    /// Number of credentials that could NOT be decrypted and would be cleared (set to empty)
    /// if the restore proceeded. Requires user confirmation before applying.
    /// </summary>
    public int WouldClear { get; init; }
}
