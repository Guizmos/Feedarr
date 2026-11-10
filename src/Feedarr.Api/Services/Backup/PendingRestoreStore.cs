using Dapper;
using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Feedarr.Api.Services.Backup;

public sealed record PendingRestoreStatus(bool Pending, string State, string? Id = null,
    string? BackupName = null, long? PreparedAtTs = null, int MissingPosters = 0, string? Error = null);

// Filesystem-only control plane. It never opens the active database during preparation.
public sealed class PendingRestoreStore
{
    private readonly string _data;
    private readonly string _database;
    private readonly string _root;
    private string Pointer => Path.Combine(_root, "pending.json");
    private string Journal => Path.Combine(_root, "operation.json");
    private string Result => Path.Combine(_root, "result.json");
    internal Action<string>? FaultPoint { get; set; }
    private sealed record Manifest([property: JsonRequired] int Version, [property: JsonRequired] string Id,
        [property: JsonRequired] string BackupName, [property: JsonRequired] long PreparedAtTs,
        [property: JsonRequired] string DatabaseHash, [property: JsonRequired] Dictionary<string, string> Posters,
        [property: JsonRequired] int MissingPosters);
    private sealed record Operation([property: JsonRequired] int Version, [property: JsonRequired] string Id,
        [property: JsonRequired] string? RescueHash);

    public PendingRestoreStore(string dataDir, string databaseName)
    {
        if (Path.GetFileName(databaseName) != databaseName || string.IsNullOrWhiteSpace(databaseName))
            throw new InvalidOperationException("Restore requires a database filename without directory components");
        _data = Path.GetFullPath(dataDir);
        _database = Path.Combine(_data, databaseName);
        _root = Path.Combine(_data, "restore-state");
        Directory.CreateDirectory(_root);
    }

    // Held for the entire process lifetime, before any active SQLite connection is opened.
    internal IDisposable AcquireProcessLease() => OpenLock("process.lock");
    private FileStream OpenLock(string name) => new(Path.Combine(_root, name), FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None);
    private string Stage(string id) => Path.Combine(_root, "stages", ValidId(id));
    private string Rescue(string id) => Path.Combine(_root, "rescue", ValidId(id));
    private static string ValidId(string id) => Guid.TryParseExact(id, "N", out _) ? id :
        throw new InvalidDataException("Invalid restore operation identity");
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) ??
        throw new InvalidDataException("Incomplete restore manifest");
    private Manifest ReadManifest(string path)
    {
        var m = Read<Manifest>(path);
        ValidId(m.Id);
        if (m.Version != 1 || m.DatabaseHash?.Length != 64 || !m.DatabaseHash.All(Uri.IsHexDigit) ||
            m.Posters is null || m.PreparedAtTs <= 0 || m.MissingPosters < 0 ||
            m.Posters.Any(file => file.Value?.Length != 64 || !file.Value.All(Uri.IsHexDigit)) ||
            string.IsNullOrEmpty(m.BackupName) || Path.GetFileName(m.BackupName) != m.BackupName)
            throw new InvalidDataException("Invalid restore manifest");
        return m;
    }

    public PendingRestoreStatus GetStatus()
    {
        try
        {
            if (File.Exists(Pointer))
            {
                var m = ReadManifest(Pointer);
                return new(true, "pending", m.Id, m.BackupName, m.PreparedAtTs, m.MissingPosters);
            }
            return File.Exists(Result) ? Read<PendingRestoreStatus>(Result) : new(false, "none");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        { return new(File.Exists(Pointer), "failed", Error: "restore_state_unreadable"); }
    }

    internal void EnsureCanPrepare(string? replacePendingId)
    {
        using var gate = OpenLock("operation.lock");
        if (File.Exists(Journal)) throw new BackupOperationException("restore application already in progress", 409);
        Manifest? previous;
        try
        {
            if (Directory.Exists(Pointer) || Directory.Exists(Journal)) throw new InvalidDataException("Restore control path is a directory");
            previous = File.Exists(Pointer) ? ReadManifest(Pointer) : null;
            if (previous is not null)
            {
                var staged = ReadManifest(Path.Combine(Stage(previous.Id), "manifest.json"));
                if (staged.Id != previous.Id || staged.DatabaseHash != previous.DatabaseHash ||
                    !File.Exists(Path.Combine(Stage(previous.Id), "incoming.db")))
                    throw new InvalidDataException("Incomplete pending restore staging");
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        { throw new BackupOperationException("État de restauration illisible ; diagnostic du staging requis avant une nouvelle préparation.", ex, 409); }
        if (previous is not null && previous.Id != replacePendingId)
            throw new BackupOperationException("pending_restore_exists: confirm replacement using its current id", 409);
        if (previous is null && replacePendingId is not null)
            throw new BackupOperationException("pending_restore_changed: refresh before replacing", 409);
        var stages = Path.Combine(_root, "stages");
        if (File.Exists(Pointer + ".tmp") || Directory.Exists(Pointer + ".tmp") ||
            File.Exists(Journal + ".tmp") || Directory.Exists(Journal + ".tmp") || File.Exists(stages) ||
            Directory.EnumerateDirectories(_root, "building-*").Any() ||
            (Directory.Exists(stages) && Directory.EnumerateDirectories(stages).Any(path => Path.GetFileName(path) != previous?.Id)))
            throw new BackupOperationException("Staging de restauration incomplet détecté ; consultez les journaux et diagnostiquez-le avant de recommencer.", 409);
    }

    internal string NewWorkDirectory()
    {
        var work = Path.Combine(_root, "building-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        return work;
    }

    internal PendingRestoreStatus Publish(string work, string backupName, string? replacePendingId,
        CancellationToken ct, string? preparationId = null)
    {
        // Work is owned by BackupService, beneath this persistent data volume.
        if (Path.GetDirectoryName(work) != _root || !Path.GetFileName(work).StartsWith("building-", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid restore staging directory");
        var db = Path.Combine(work, "incoming.db");
        var posters = Path.Combine(work, "posters");
        FaultPoint?.Invoke("copying-posters");
        CopyTree(Path.Combine(_data, "posters"), posters, ct, tolerateDisappearing: true);
        var missing = RepairUnavailablePosters(db, posters);
        using (var conn = OpenDatabase(db, readOnly: false)) conn.Execute("PRAGMA journal_mode=DELETE;");
        VerifyDatabase(db);
        FlushFile(db);
        var files = Inventory(posters);
        var id = preparationId is null ? Guid.NewGuid().ToString("N") : ValidId(preparationId);
        var manifest = new Manifest(1, id, Path.GetFileName(backupName), DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Hash(db), files, missing);
        WriteJson(Path.Combine(work, "manifest.json"), manifest);
        ct.ThrowIfCancellationRequested();
        FaultPoint?.Invoke("prepared");
        using var gate = OpenLock("operation.lock");
        if (File.Exists(Journal)) throw new BackupOperationException("restore application already in progress", 409);
        Manifest? previous = File.Exists(Pointer) ? ReadManifest(Pointer) : null;
        if (previous is not null && previous.Id != replacePendingId)
            throw new BackupOperationException("pending_restore_exists: confirm replacement using its current id", 409);
        if (previous is null && replacePendingId is not null)
            throw new BackupOperationException("pending_restore_changed: refresh before replacing", 409);
        Directory.CreateDirectory(Path.Combine(_root, "stages"));
        Directory.Move(work, Stage(id));
        SyncDirectory(Path.Combine(_root, "stages"));
        SyncDirectory(_data); // persist the restore-state directory entry before publishing intent
        WriteJson(Pointer, manifest); // only the complete stage becomes visible
        FaultPoint?.Invoke("published");
        if (previous is not null) DeleteUnreferencedStage(previous.Id);
        return GetStatus();
    }

    public void Cancel(string expectedId)
    {
        using var gate = OpenLock("operation.lock");
        if (File.Exists(Journal)) throw new BackupOperationException("restore application already in progress", 409);
        if (!File.Exists(Pointer)) throw new BackupOperationException("no pending restore", 409);
        var m = ReadManifest(Pointer);
        if (m.Id != expectedId) throw new BackupOperationException("pending_restore_changed: refresh before cancelling", 409);
        WriteJson(Result, new PendingRestoreStatus(false, "cancelled", m.Id, m.BackupName, m.PreparedAtTs));
        File.Delete(Pointer);
        SyncDirectory(_root);
        DeleteUnreferencedStage(m.Id);
    }

    // Called only by Program before constructing the application service provider.
    internal void ApplyAtStartup()
    {
        using var gate = OpenLock("operation.lock");
        if (Directory.Exists(Pointer) || Directory.Exists(Journal))
            throw new InvalidDataException("Restore manifest/journal is a directory, startup refused");
        if (!File.Exists(Pointer) && !File.Exists(Journal))
        {
            CleanupCompletedWork();
            return;
        }
        try
        {
            Operation? operation = File.Exists(Journal) ? Read<Operation>(Journal) : null;
            var manifest = operation is null ? ReadManifest(Pointer) :
                ReadManifest(Path.Combine(Stage(ValidId(operation.Id)), "manifest.json"));
            if (operation is not null && (operation.Version != 1 || operation.Id != manifest.Id))
                throw new InvalidDataException("Restore journal does not match staging");
            if (File.Exists(Pointer) && ReadManifest(Pointer).Id != manifest.Id)
                throw new InvalidDataException("Conflicting pending restore and journal");
            var stage = Stage(manifest.Id);
            var incoming = Path.Combine(stage, "incoming.db");
            if ((File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(incoming) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Symlink in restore staging");
            if (Hash(incoming) != manifest.DatabaseHash) throw new InvalidDataException("Staged database checksum mismatch");
            VerifyDatabase(incoming);
            VerifyInventory(Path.Combine(stage, "posters"), manifest.Posters);
            FaultPoint?.Invoke("validated");
            var rescue = Rescue(manifest.Id);
            Directory.CreateDirectory(rescue);
            SyncDirectory(Path.GetDirectoryName(rescue)!);
            var rescueDb = Path.Combine(rescue, "original.db");
            if (operation is null)
            {
                string? hash = null;
                if (File.Exists(_database))
                {
                    var rescueWork = Path.Combine(rescue, "copying.db");
                    File.Delete(rescueWork);
                    using (var source = OpenDatabase(_database, readOnly: false))
                    using (var destination = OpenDatabase(rescueWork, readOnly: false, create: true))
                        source.BackupDatabase(destination); // includes committed WAL data; no copy/checkpoint fallback
                    VerifyDatabase(rescueWork);
                    FlushFile(rescueWork);
                    File.Move(rescueWork, rescueDb, overwrite: true);
                    hash = Hash(rescueDb);
                    SyncDirectory(rescue);
                }
                else if (File.Exists(rescueDb))
                    throw new InvalidDataException("Active database missing during interrupted rescue; original copy retained");
                FaultPoint?.Invoke("rescue-copied");
                operation = new Operation(1, manifest.Id, hash);
                WriteJson(Journal, operation); // durable roll-forward intent, before modifying active files
            }
            if (operation.RescueHash is not null)
            {
                if (Hash(rescueDb) != operation.RescueHash) throw new InvalidDataException("Rescue database checksum mismatch");
                VerifyDatabase(rescueDb);
            }
            FaultPoint?.Invoke("journaled");
            var install = Path.Combine(stage, "install.db");
            CopyDurable(incoming, install);
            var installPosters = Path.Combine(stage, "install-posters");
            if (Directory.Exists(installPosters)) Directory.Delete(installPosters, true);
            CopyTree(Path.Combine(stage, "posters"), installPosters, default);
            VerifyInventory(installPosters, manifest.Posters);
            var activePosters = Path.Combine(_data, "posters");
            var oldPosters = Path.Combine(rescue, "posters");
            if (Directory.Exists(activePosters))
            {
                if (!Directory.Exists(oldPosters)) Directory.Move(activePosters, oldPosters);
                else Directory.Delete(activePosters, true); // previous interrupted install; originals remain in rescue
            }
            SyncDirectory(_data);
            SyncDirectory(rescue);
            FaultPoint?.Invoke("posters-moved");
            Directory.Move(installPosters, activePosters);
            SyncDirectory(_data);
            FaultPoint?.Invoke("posters-installed");
            // Only startup, with process lease held and all startup-only connections disposed.
            File.Delete(_database + "-wal");
            File.Delete(_database + "-shm");
            File.Move(install, _database, overwrite: true);
            SyncDirectory(_data);
            FaultPoint?.Invoke("database-installed");
            VerifyDatabase(_database);
            if (Hash(_database) != manifest.DatabaseHash) throw new InvalidDataException("Installed database checksum mismatch");
            VerifyInventory(activePosters, manifest.Posters);
            FaultPoint?.Invoke("verified");
            WriteJson(Result, new PendingRestoreStatus(false, "applied", manifest.Id, manifest.BackupName,
                manifest.PreparedAtTs, manifest.MissingPosters));
            File.Delete(Pointer);
            SyncDirectory(_root);
            FaultPoint?.Invoke("pointer-cleared");
            File.Delete(Journal);
            SyncDirectory(_root);
            FaultPoint?.Invoke("committed");
            DeleteUnreferencedStage(manifest.Id);
            // Rescue database and original posters are deliberately retained, outside backup purge.
        }
        catch
        {
            if (File.Exists(Pointer) || File.Exists(Journal))
                WriteJson(Result, new PendingRestoreStatus(File.Exists(Pointer), "failed", Error: "restore_startup_failed"));
            throw; // never expose HTTP or start workers on an uncertain database
        }
    }

    private void CleanupCompletedWork()
    {
        try
        {
            foreach (var work in Directory.EnumerateDirectories(_root, "building-*"))
                if (Guid.TryParseExact(Path.GetFileName(work)["building-".Length..], "N", out _))
                    Directory.Delete(work, true);
            if (File.Exists(Result))
            {
                var status = Read<PendingRestoreStatus>(Result);
                if (status.State is "applied" or "cancelled" && status.Id is not null)
                    DeleteUnreferencedStage(status.Id);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine($"[RESTORE] Unreferenced staging cleanup deferred: {ex.GetType().Name}"); }
    }

    private void DeleteUnreferencedStage(string id)
    {
        if (File.Exists(Journal) || (File.Exists(Pointer) && ReadManifest(Pointer).Id == id)) return;
        var path = Stage(id);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        var parent = Path.GetDirectoryName(path)!;
        if (Directory.Exists(parent)) SyncDirectory(parent);
    }

    internal static SqliteConnection OpenDatabase(string path, bool readOnly, bool create = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite
        }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }
    internal static void VerifyDatabase(string path)
    {
        using var db = OpenDatabase(path, readOnly: true);
        if (!db.Query<string>("PRAGMA integrity_check;").SequenceEqual(new[] { "ok" }))
            throw new InvalidDataException("Restore SQLite integrity check failed");
    }
    private static string Hash(string path)
    { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static string SafeChild(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') ||
            relative.Split('/').Any(part => part is ".." or "." or ""))
            throw new InvalidDataException("Invalid staged poster path");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Poster path escapes staging");
        return path;
    }
    private static Dictionary<string, string> Inventory(string root)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        void Visit(string directory)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Symlink in staged posters");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Symlink in staged posters");
                if ((attributes & FileAttributes.Directory) != 0) Visit(path);
                else files.Add(Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), Hash(path));
            }
        }
        Visit(root);
        return files;
    }
    private static void VerifyInventory(string root, Dictionary<string, string> expected)
    {
        foreach (var name in expected.Keys) SafeChild(root, name);
        var actual = Inventory(root);
        if (actual.Count != expected.Count || expected.Any(file => !actual.TryGetValue(file.Key, out var hash) || hash != file.Value))
            throw new InvalidDataException("Staged poster inventory/checksum mismatch");
    }
    private static void CopyTree(string source, string destination, CancellationToken ct, bool tolerateDisappearing = false)
    {
        Directory.CreateDirectory(destination);
        if (!Directory.Exists(source)) { SyncDirectory(destination); return; }
        // Never traverse symlinks outside the data volume.
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Symlink in poster storage");
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Symlink in poster storage");
                var target = Path.Combine(destination, Path.GetFileName(entry));
                if ((attributes & FileAttributes.Directory) != 0) CopyTree(entry, target, ct, tolerateDisappearing);
                else CopyDurable(entry, target);
            }
            catch (FileNotFoundException) when (tolerateDisappearing) { }
            catch (DirectoryNotFoundException) when (tolerateDisappearing) { }
        }
        SyncDirectory(destination);
    }
    private static void CopyDurable(string source, string destination)
    {
        using var input = File.OpenRead(source);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }
    private static void FlushFile(string path)
    { using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); file.Flush(true); }
    private static void WriteJson<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, value); file.Flush(true); }
        File.Move(temporary, path, overwrite: true);
        SyncDirectory(Path.GetDirectoryName(path)!);
    }
    private static void SyncDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return; // NTFS rename + flushed files; no portable directory fsync API
        var handle = open(path, 0);
        if (handle < 0) throw new IOException("Unable to open restore directory for synchronization");
        try { if (fsync(handle) != 0) throw new IOException("Unable to synchronize restore directory"); }
        finally { close(handle); }
    }
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int fsync(int descriptor);
    [DllImport("libc")] private static extern int close(int descriptor);

    private static int RepairUnavailablePosters(string dbPath, string posters)
    {
        using var db = OpenDatabase(dbPath, readOnly: false);
        var repaired = 0;
        foreach (var table in new[] { "releases", "media_entities", "poster_matches" })
        {
            var columns = db.Query($"PRAGMA table_info({table});").Select(row => (string)row.name).ToHashSet();
            if (!columns.Contains("poster_file")) continue;
            var rows = db.Query($"SELECT rowid AS Id, poster_file AS File FROM {table} WHERE COALESCE(poster_file,'')<>''");
            foreach (var row in rows)
            {
                var valid = false;
                try { valid = File.Exists(SafeChild(posters, (string)row.File)); } catch (InvalidDataException) { }
                if (valid) continue;
                repaired++;
                if (table == "poster_matches") db.Execute("DELETE FROM poster_matches WHERE rowid=@id", new { id = (long)row.Id });
                else
                {
                    var reset = columns.Where(col => col is "poster_file" or "poster_key" or "poster_store_dir")
                        .Select(col => col + "=NULL");
                    db.Execute($"UPDATE {table} SET {string.Join(',', reset)} WHERE rowid=@id", new { id = (long)row.Id });
                }
            }
            if (columns.Contains("poster_store_dir"))
            {
                foreach (var row in db.Query($"SELECT rowid AS Id, poster_store_dir AS Dir FROM {table} WHERE COALESCE(poster_store_dir,'')<>''"))
                {
                    var valid = false;
                    try { var directory = SafeChild(Path.Combine(posters, "store"), (string)row.Dir);
                        valid = Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any(); } catch (InvalidDataException) { }
                    if (valid) continue;
                    repaired++;
                    db.Execute($"UPDATE {table} SET poster_store_dir=NULL,poster_key=NULL WHERE rowid=@id", new { id = (long)row.Id });
                }
            }
        }
        return repaired;
    }
}
