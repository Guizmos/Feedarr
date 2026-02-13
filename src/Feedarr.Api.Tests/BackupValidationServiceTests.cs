using System.IO.Compression;
using System.Text.Json;
using Feedarr.Api.Services.Backup;

namespace Feedarr.Api.Tests;

public sealed class BackupValidationServiceTests
{
    [Fact]
    public void TryValidateArchiveForRestore_RejectsMultipleDatabaseEntriesIncludingExpectedName()
    {
        using var workspace = new TestWorkspace();
        var zipPath = Path.Combine(workspace.RootDir, "multiple-dbs.zip");
        CreateArchive(
            zipPath,
            new { version = "1.0.0", backupFormatVersion = 1, dbSha256 = new string('a', 64) },
            ("feedarr.db", "db1"),
            ("other.db", "db2"));

        using var archive = ZipFile.OpenRead(zipPath);
        var service = new BackupValidationService();

        var ok = service.TryValidateArchiveForRestore(
            archive,
            "feedarr.db",
            "1.0.0",
            out _,
            out _,
            out var error);

        Assert.False(ok);
        Assert.Contains("multiple .db files", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("feedarr.db", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("other.db", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidateArchiveForRestore_RejectsMissingDatabaseEntry()
    {
        using var workspace = new TestWorkspace();
        var zipPath = Path.Combine(workspace.RootDir, "no-db.zip");
        CreateArchive(
            zipPath,
            new { version = "1.0.0", backupFormatVersion = 1, dbSha256 = new string('a', 64) });

        using var archive = ZipFile.OpenRead(zipPath);
        var service = new BackupValidationService();

        var ok = service.TryValidateArchiveForRestore(
            archive,
            "feedarr.db",
            "1.0.0",
            out _,
            out _,
            out var error);

        Assert.False(ok);
        Assert.Contains("no .db file", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidateArchiveForRestore_RejectsNewerFormatVersion()
    {
        using var workspace = new TestWorkspace();
        var zipPath = Path.Combine(workspace.RootDir, "newer-format.zip");
        CreateArchive(
            zipPath,
            new { version = "1.0.0", backupFormatVersion = BackupValidationService.CurrentBackupFormatVersion + 1 },
            ("feedarr.db", "db"));

        using var archive = ZipFile.OpenRead(zipPath);
        var service = new BackupValidationService();

        var ok = service.TryValidateArchiveForRestore(
            archive,
            "feedarr.db",
            "1.0.0",
            out _,
            out _,
            out var error);

        Assert.False(ok);
        Assert.Contains("unsupported", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidateArchiveForRestore_RejectsInvalidChecksumMetadata()
    {
        using var workspace = new TestWorkspace();
        var zipPath = Path.Combine(workspace.RootDir, "bad-checksum.zip");
        CreateArchive(
            zipPath,
            new { version = "1.0.0", backupFormatVersion = 1, dbSha256 = "invalid" },
            ("feedarr.db", "db"));

        using var archive = ZipFile.OpenRead(zipPath);
        var service = new BackupValidationService();

        var ok = service.TryValidateArchiveForRestore(
            archive,
            "feedarr.db",
            "1.0.0",
            out _,
            out _,
            out var error);

        Assert.False(ok);
        Assert.Contains("dbSha256", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidateArchiveForRestore_RejectsMissingChecksumMetadata()
    {
        using var workspace = new TestWorkspace();
        var zipPath = Path.Combine(workspace.RootDir, "missing-checksum.zip");
        CreateArchive(
            zipPath,
            new { version = "1.0.0", backupFormatVersion = 1 },
            ("feedarr.db", "db"));

        using var archive = ZipFile.OpenRead(zipPath);
        var service = new BackupValidationService();

        var ok = service.TryValidateArchiveForRestore(
            archive,
            "feedarr.db",
            "1.0.0",
            out _,
            out _,
            out var error);

        Assert.False(ok);
        Assert.Contains("missing dbSha256", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidateArchiveForRestore_ReturnsChecksumDetailsWhenValid()
    {
        using var workspace = new TestWorkspace();
        var checksum = new string('b', 64);
        var zipPath = Path.Combine(workspace.RootDir, "valid.zip");
        CreateArchive(
            zipPath,
            new { version = "1.0.0", backupFormatVersion = 1, dbSha256 = checksum },
            ("feedarr.db", "db"));

        using var archive = ZipFile.OpenRead(zipPath);
        var service = new BackupValidationService();

        var ok = service.TryValidateArchiveForRestore(
            archive,
            "feedarr.db",
            "1.0.0",
            out var dbEntryName,
            out var details,
            out var error);

        Assert.True(ok, error);
        Assert.Equal("feedarr.db", dbEntryName);
        Assert.Equal(checksum, details.DbSha256);
    }

    private static void CreateArchive(string zipPath, object info, params (string Name, string Content)[] entries)
    {
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        AddJsonEntry(archive, "info.json", info);

        foreach (var entry in entries)
        {
            var zipEntry = archive.CreateEntry(entry.Name);
            using var stream = zipEntry.Open();
            using var writer = new StreamWriter(stream);
            writer.Write(entry.Content);
        }
    }

    private static void AddJsonEntry(ZipArchive archive, string name, object payload)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(JsonSerializer.Serialize(payload));
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            RootDir = Path.Combine(Path.GetTempPath(), "feedarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootDir);
        }

        public string RootDir { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootDir))
                    Directory.Delete(RootDir, true);
            }
            catch
            {
            }
        }
    }
}
