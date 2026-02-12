using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Feedarr.Api.Services.Backup;

public sealed class BackupValidationService
{
    public const int CurrentBackupFormatVersion = 1;
    private const long MaxInfoJsonBytes = 64 * 1024;
    private const long MaxDatabaseBytes = 2L * 1024 * 1024 * 1024; // 2 GB

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public bool TryValidateArchiveForRestore(
        ZipArchive archive,
        string expectedDbFileName,
        string currentAppVersion,
        out string dbEntryName,
        out BackupValidationDetails details,
        out string error)
    {
        _ = expectedDbFileName;
        dbEntryName = "";
        details = new BackupValidationDetails();
        error = "";

        var infoEntry = archive.GetEntry("info.json");
        if (infoEntry is null)
        {
            error = "backup info.json missing";
            return false;
        }

        if (infoEntry.Length <= 0 || infoEntry.Length > MaxInfoJsonBytes)
        {
            error = "backup info.json invalid";
            return false;
        }

        BackupInfoPayload? info;
        try
        {
            using var stream = infoEntry.Open();
            info = JsonSerializer.Deserialize<BackupInfoPayload>(stream, JsonOptions);
        }
        catch
        {
            error = "backup info.json unreadable";
            return false;
        }

        if (info is null)
        {
            error = "backup info.json unreadable";
            return false;
        }

        if (info.BackupFormatVersion <= 0)
        {
            error = $"backup format version invalid ({info.BackupFormatVersion})";
            return false;
        }

        if (info.BackupFormatVersion > CurrentBackupFormatVersion)
        {
            error = $"backup format version unsupported ({info.BackupFormatVersion})";
            return false;
        }

        if (string.IsNullOrWhiteSpace(info.DbSha256))
        {
            error = "backup info.json missing dbSha256";
            return false;
        }

        if (!IsValidSha256Hex(info.DbSha256))
        {
            error = "backup info.json contains invalid dbSha256";
            return false;
        }

        if (!IsVersionCompatible(info.Version, currentAppVersion, out var versionError))
        {
            error = versionError;
            return false;
        }

        var dbEntries = archive.Entries
            .Where(e =>
                !string.IsNullOrWhiteSpace(e.Name) &&
                e.FullName.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (dbEntries.Count == 0)
        {
            error = "backup archive contains no .db file";
            return false;
        }

        if (dbEntries.Count > 1)
        {
            var dbList = string.Join(", ", dbEntries.Select(e => e.FullName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            error = $"backup archive contains multiple .db files: {dbList}";
            return false;
        }

        var dbEntry = dbEntries[0];
        if (dbEntry.Length <= 0)
        {
            error = "backup database is empty";
            return false;
        }

        if (dbEntry.Length > MaxDatabaseBytes)
        {
            error = "backup database too large";
            return false;
        }

        dbEntryName = dbEntry.FullName;
        details = new BackupValidationDetails
        {
            DbSha256 = info.DbSha256.ToLowerInvariant()
        };

        return true;
    }

    public bool IsSupportedDatabaseSize(long bytes)
        => bytes > 0 && bytes <= MaxDatabaseBytes;

    private static bool IsVersionCompatible(string? backupVersion, string currentVersion, out string error)
    {
        error = "";

        var backupMajor = ParseMajorVersion(backupVersion);
        var currentMajor = ParseMajorVersion(currentVersion);

        if (backupMajor is null || currentMajor is null)
            return true; // We keep compatibility when version string cannot be parsed.

        if (backupMajor > currentMajor)
        {
            error = $"backup version {backupVersion} is newer than current app version {currentVersion}";
            return false;
        }

        return true;
    }

    private static int? ParseMajorVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var match = Regex.Match(value, @"(\d+)");
        if (!match.Success)
            return null;

        if (int.TryParse(match.Groups[1].Value, out var major))
            return major;

        return null;
    }

    private static bool IsValidSha256Hex(string value)
    {
        if (value.Length != 64)
            return false;

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var isHex =
                (c >= '0' && c <= '9') ||
                (c >= 'a' && c <= 'f') ||
                (c >= 'A' && c <= 'F');
            if (!isHex)
                return false;
        }

        return true;
    }

    private sealed class BackupInfoPayload
    {
        public string? Version { get; set; }
        public int BackupFormatVersion { get; set; }
        public string? DbSha256 { get; set; }
    }
}

public sealed class BackupValidationDetails
{
    public string DbSha256 { get; init; } = "";
}
