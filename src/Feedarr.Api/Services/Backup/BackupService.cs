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

public sealed class BackupService
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
        ILogger<BackupService> logger)
    {
        _db = db;
        _env = env;
        _opts = opts.Value;
        _settings = settings;
        _validation = validation;
        _coordinator = coordinator;
        _apiKeyProtection = apiKeyProtection;
        _logger = logger;
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

    public int PurgeBackups()
    {
        EnsureRestartNotRequired();
        return _coordinator.RunExclusive("purge", null, () =>
        {
            Directory.CreateDirectory(BackupDirAbs);
            var files = new DirectoryInfo(BackupDirAbs)
                .EnumerateFiles("*.zip", SearchOption.TopDirectoryOnly)
                .ToList();

            var deleted = 0;
            foreach (var file in files)
            {
                var path = file.FullName;
                TryDeleteFile(path);
                if (!System.IO.File.Exists(path))
                    deleted++;
            }

            return deleted;
        });
    }

    public BackupFileDto CreateBackup(string appVersion)
    {
        EnsureRestartNotRequired();
        return _coordinator.RunExclusive("create", null, () =>
        {
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
                throw new BackupOperationException(ex.Message, ex);
            }
            finally
            {
                TryDeleteFile(tempDbPath);
            }

            var info = new FileInfo(zipPath);
            return new BackupFileDto
            {
                Name = info.Name,
                SizeBytes = info.Length,
                CreatedAtTs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds()
            };
        });
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

    public void DeleteBackup(string name)
    {
        _coordinator.RunExclusive("delete", name, () =>
        {
            var (_, path) = GetExistingBackupFile(name);
            TryDeleteFile(path);
            if (System.IO.File.Exists(path))
                throw new BackupOperationException("backup delete failed");

            return true;
        });
    }

    public BackupRestoreResult RestoreBackup(string name, string currentAppVersion)
    {
        EnsureRestartNotRequired();
        return _coordinator.RunExclusive("restore", name, () =>
        {
            var (_, backupPath) = GetExistingBackupFile(name);
            var operationId = Guid.NewGuid().ToString("N");
            var workDir = Path.Combine(BackupDirAbs, $"restore-{operationId}");
            var extractedDbPath = Path.Combine(workDir, "incoming.db");
            var credentialReport = CredentialRestoreReport.Empty;

            Directory.CreateDirectory(workDir);

            try
            {
                using var archive = ZipFile.OpenRead(backupPath);
                if (!_validation.TryValidateArchiveForRestore(
                        archive,
                        _opts.DbFileName,
                        currentAppVersion,
                        out var dbEntryName,
                        out var details,
                        out var validationError))
                {
                    throw new BackupOperationException(validationError, StatusCodes.Status400BadRequest);
                }

                var dbEntry = archive.GetEntry(dbEntryName);
                if (dbEntry is null)
                    throw new BackupOperationException("backup database missing", StatusCodes.Status400BadRequest);

                ExtractEntryControlled(dbEntry, extractedDbPath);

                var extractedHash = ComputeFileSha256(extractedDbPath);
                if (!string.Equals(extractedHash, details.DbSha256, StringComparison.OrdinalIgnoreCase))
                    throw new BackupOperationException("backup database checksum mismatch", StatusCodes.Status400BadRequest);

                VerifySqliteIntegrity(extractedDbPath);
                credentialReport = NormalizeRestoredCredentials(extractedDbPath);
                VerifySqliteIntegrity(extractedDbPath);

                CreatePreRestoreBackup(currentAppVersion);
                ReplaceDatabaseAtomically(extractedDbPath);
                MarkRestartRequired();

                if (credentialReport.ClearedUndecryptable > 0)
                {
                    _logger.LogWarning(
                        "Backup restore completed with {Cleared} credentials cleared because they could not be decrypted with current key ring",
                        credentialReport.ClearedUndecryptable);
                }

                _logger.LogInformation("Backup restore succeeded from {BackupPath}", backupPath);
            }
            catch (BackupOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backup restore failed for {BackupName}", name);
                throw new BackupOperationException(ex.Message, ex);
            }
            finally
            {
                TryDeleteFile(extractedDbPath);
                TryDeleteDirectory(workDir);
            }

            return new BackupRestoreResult
            {
                ReencryptedCredentials = credentialReport.Reencrypted,
                ClearedUndecryptableCredentials = credentialReport.ClearedUndecryptable
            };
        });
    }

    public BackupOperationStateDto GetOperationState()
    {
        var state = _coordinator.GetState();
        state.NeedsRestart = IsRestartRequired();
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

    private void MarkRestartRequired()
    {
        Directory.CreateDirectory(DataDirAbs);
        System.IO.File.WriteAllText(RestartRequiredFlagPath, DateTimeOffset.UtcNow.ToString("O"));
    }

    private void CreatePreRestoreBackup(string appVersion)
    {
        if (!System.IO.File.Exists(DbPathAbs))
            return;

        Directory.CreateDirectory(BackupDirAbs);

        var version = SanitizeFileFragment(appVersion);
        if (string.IsNullOrWhiteSpace(version)) version = "0.0.0";

        var timestamp = DateTime.UtcNow;
        var baseName = $"feedarr_prerestore_v{version}_{timestamp:yyyy.MM.dd_HH.mm.ss}";
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
            AddJsonEntry(archive, "info.json", BuildBackupInfo(timestamp, appVersion, "pre-restore", dbSha256, dbSizeBytes));
        }
        catch (Exception ex)
        {
            TryDeleteFile(zipPath);
            throw new BackupOperationException("unable to create pre-restore backup", ex);
        }
        finally
        {
            TryDeleteFile(tempDbPath);
        }
    }

    private void ReplaceDatabaseAtomically(string sourcePath)
    {
        Directory.CreateDirectory(DataDirAbs);

        var stagePath = Path.Combine(DataDirAbs, $"restore-stage-{Guid.NewGuid():N}.db");
        var rollbackPath = Path.Combine(DataDirAbs, $"restore-rollback-{Guid.NewGuid():N}.db");

        TryDeleteFile(stagePath);
        TryDeleteFile(rollbackPath);

        try
        {
            System.IO.File.Copy(sourcePath, stagePath, true);
            VerifySqliteIntegrity(stagePath);
            CheckpointCurrentDatabase();

            if (!System.IO.File.Exists(DbPathAbs))
            {
                System.IO.File.Move(stagePath, DbPathAbs, true);
            }
            else
            {
                try
                {
                    System.IO.File.Replace(stagePath, DbPathAbs, rollbackPath, true);
                    TryDeleteFile(rollbackPath);
                }
                catch (PlatformNotSupportedException)
                {
                    ReplaceWithMove(stagePath, rollbackPath);
                }
                catch (IOException)
                {
                    ReplaceWithMove(stagePath, rollbackPath);
                }
            }

            TryDeleteFile(DbPathAbs + "-wal");
            TryDeleteFile(DbPathAbs + "-shm");
        }
        finally
        {
            TryDeleteFile(stagePath);
            TryDeleteFile(rollbackPath);
        }
    }

    private void ReplaceWithMove(string stagePath, string rollbackPath)
    {
        var movedCurrent = false;

        try
        {
            if (System.IO.File.Exists(DbPathAbs))
            {
                System.IO.File.Move(DbPathAbs, rollbackPath, true);
                movedCurrent = true;
            }

            System.IO.File.Move(stagePath, DbPathAbs, true);
            if (movedCurrent)
                TryDeleteFile(rollbackPath);
        }
        catch
        {
            if (movedCurrent && System.IO.File.Exists(rollbackPath))
            {
                TryDeleteFile(DbPathAbs);
                System.IO.File.Move(rollbackPath, DbPathAbs, true);
            }

            throw;
        }
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
                Cache = SqliteCacheMode.Shared,
                Pooling = false
            }.ToString();

            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteScalar();
        }
        catch
        {
        }
    }

    private static void VerifySqliteIntegrity(string dbPath)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        using var conn = new SqliteConnection(cs);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(cmd.ExecuteScalar())?.Trim();

        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new BackupOperationException($"backup database integrity check failed: {result}");
    }

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
            using var conn = _db.Open();
            var safePath = destinationPath.Replace("'", "''");
            conn.Execute($"VACUUM INTO '{safePath}';");
            return;
        }
        catch
        {
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
            Mode = SqliteOpenMode.ReadWrite
        }.ToString();

        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var tx = conn.BeginTransaction();

        var report = CredentialRestoreReport.Empty;
        NormalizeCredentialsColumn(conn, tx, "sources", "api_key", ref report);
        NormalizeCredentialsColumn(conn, tx, "providers", "api_key", ref report);
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
                if (_apiKeyProtection.TryUnprotect(currentValue, out var plainText))
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
}
