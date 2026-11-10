using Dapper;
using Feedarr.Api.Services.Backup;
using Microsoft.Data.Sqlite;

namespace Feedarr.Api.Tests;

public sealed class PendingRestoreStoreTests
{
    [Fact]
    public void NoManifest_DoesNotOpenTheActiveDatabase()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Active, "not SQLite: must not be opened");
        fixture.Store.ApplyAtStartup();
        Assert.Equal("not SQLite: must not be opened", File.ReadAllText(fixture.Active));
        Assert.Equal("none", fixture.Store.GetStatus().State);
    }

    [Fact]
    public void PreparationAndProlongedWait_LeaveLiveDatabaseAndConnectionsAlone()
    {
        using var fixture = new Fixture();
        fixture.CreateDatabase(fixture.Active, "live");
        using (var live = PendingRestoreStore.OpenDatabase(fixture.Active, readOnly: false))
        {
            fixture.Prepare();
            Assert.Equal("live", live.ExecuteScalar<string>("SELECT value FROM marker"));
            live.Execute("UPDATE marker SET value='weeks later'");
            var pointer = Path.Combine(fixture.Root, "restore-state", "pending.json");
            File.SetLastWriteTimeUtc(pointer, DateTime.UtcNow.AddDays(-35));
            Assert.True(new PendingRestoreStore(fixture.Root, "feedarr.db").GetStatus().Pending);
            Assert.Equal("weeks later", live.ExecuteScalar<string>("SELECT value FROM marker"));
        }
        fixture.Store.ApplyAtStartup();
        Assert.Equal("selected backup", fixture.ReadMarker(fixture.Active));
        var rescue = Directory.GetFiles(Path.Combine(fixture.Root, "restore-state", "rescue"), "original.db", SearchOption.AllDirectories).Single();
        Assert.Equal("weeks later", fixture.ReadMarker(rescue));
    }

    [Fact]
    public void ReplacementRequiresCurrentIdentity_AndCancelNeverTouchesActiveDatabase()
    {
        using var fixture = new Fixture();
        fixture.CreateDatabase(fixture.Active, "live");
        var first = fixture.Prepare();
        var conflict = Assert.Throws<BackupOperationException>(() => fixture.Prepare());
        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal(first.Id, fixture.Store.GetStatus().Id);
        var second = fixture.Prepare(first.Id);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Throws<BackupOperationException>(() => fixture.Store.Cancel(first.Id!));
        fixture.Store.Cancel(second.Id!);
        Assert.False(fixture.Store.GetStatus().Pending);
        Assert.Equal("cancelled", fixture.Store.GetStatus().State);
        Assert.Equal("live", fixture.ReadMarker(fixture.Active));
        fixture.Store.ApplyAtStartup();
        Assert.Equal("live", fixture.ReadMarker(fixture.Active));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("database")]
    [InlineData("poster")]
    [InlineData("manifest")]
    public void InvalidStaging_StopsStartupWithoutReplacingActiveDatabase(string corruption)
    {
        using var fixture = new Fixture();
        fixture.CreateDatabase(fixture.Active, "live");
        fixture.CreatePoster("kept.jpg", "poster data");
        var pending = fixture.Prepare();
        var stage = Path.Combine(fixture.Root, "restore-state", "stages", pending.Id!);
        switch (corruption)
        {
            case "missing": Directory.Delete(stage, true); break;
            case "database": File.AppendAllText(Path.Combine(stage, "incoming.db"), "tamper"); break;
            case "poster": File.AppendAllText(Path.Combine(stage, "posters", "kept.jpg"), "tamper"); break;
            case "manifest": File.WriteAllText(Path.Combine(fixture.Root, "restore-state", "pending.json"), "{}"); break;
        }
        Assert.ThrowsAny<Exception>(() => fixture.Store.ApplyAtStartup());
        Assert.Equal("live", fixture.ReadMarker(fixture.Active));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "restore-state", "pending.json")));
    }

    [Theory]
    [InlineData("validated")]
    [InlineData("rescue-copied")]
    [InlineData("journaled")]
    [InlineData("posters-moved")]
    [InlineData("posters-installed")]
    [InlineData("database-installed")]
    [InlineData("verified")]
    [InlineData("pointer-cleared")]
    [InlineData("committed")]
    public void InterruptionAtCriticalBoundaries_IsRecoveredIdempotently(string boundary)
    {
        using var fixture = new Fixture();
        fixture.CreateDatabase(fixture.Active, "original");
        fixture.CreatePoster("kept.jpg", "old bytes");
        fixture.Prepare();
        fixture.CreatePoster("later.jpg", "added while waiting");
        fixture.Store.FaultPoint = phase => { if (phase == boundary) throw new IOException("simulated abrupt stop"); };
        Assert.Throws<IOException>(() => fixture.Store.ApplyAtStartup());
        var restart = new PendingRestoreStore(fixture.Root, "feedarr.db");
        restart.ApplyAtStartup();
        Assert.Equal("selected backup", fixture.ReadMarker(fixture.Active));
        Assert.Equal("applied", restart.GetStatus().State);
        Assert.False(restart.GetStatus().Pending);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "posters", "kept.jpg")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "posters", "later.jpg")));
        restart.ApplyAtStartup();
        Assert.Equal("selected backup", fixture.ReadMarker(fixture.Active));
        var rescue = Directory.GetFiles(Path.Combine(fixture.Root, "restore-state", "rescue"), "original.db", SearchOption.AllDirectories).Single();
        Assert.Equal("original", fixture.ReadMarker(rescue));
        PendingRestoreStore.VerifyDatabase(rescue);
    }

    [Fact]
    public void CommittedWalData_IsIncludedInRescueDatabase()
    {
        using var fixture = new Fixture();
        fixture.CreateDatabase(fixture.Active, "original");
        fixture.Prepare();
        // Leave a committed, uncheckpointed WAL snapshot, then close without last-close checkpoint.
        using (var active = PendingRestoreStore.OpenDatabase(fixture.Active, readOnly: false))
        {
            active.Execute("PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
            active.Execute("UPDATE marker SET value='committed WAL data'");
            // Copy while the connection is open to reproduce files left by abrupt process death.
            File.Copy(fixture.Active, fixture.Active + ".crash");
            File.Copy(fixture.Active + "-wal", fixture.Active + ".crash-wal");
        }
        File.Move(fixture.Active + ".crash", fixture.Active, true);
        File.Move(fixture.Active + ".crash-wal", fixture.Active + "-wal", true);
        File.Delete(fixture.Active + "-shm");
        fixture.Store.ApplyAtStartup();
        var rescue = Directory.GetFiles(Path.Combine(fixture.Root, "restore-state", "rescue"), "original.db", SearchOption.AllDirectories).Single();
        Assert.Equal("committed WAL data", fixture.ReadMarker(rescue));
        Assert.Equal("selected backup", fixture.ReadMarker(fixture.Active));
    }

    [Fact]
    public void StagedPostersSurviveLiveGarbageCollection_AndMissingReferencesAreReported()
    {
        using var fixture = new Fixture();
        fixture.CreatePoster("kept.jpg", "pinned bytes");
        var pending = fixture.Prepare();
        Assert.Equal(1, pending.MissingPosters);
        Directory.Delete(Path.Combine(fixture.Root, "posters"), true);
        fixture.Store.ApplyAtStartup();
        Assert.Equal("pinned bytes", File.ReadAllText(Path.Combine(fixture.Root, "posters", "kept.jpg")));
        using var db = PendingRestoreStore.OpenDatabase(fixture.Active, true);
        Assert.Equal("kept.jpg", db.ExecuteScalar<string>("SELECT poster_file FROM releases WHERE id=1"));
        Assert.Null(db.ExecuteScalar<string>("SELECT poster_file FROM releases WHERE id=2"));
    }

    [Fact]
    public void InterruptedPreparation_DoesNotReplaceAnExistingPendingRestore()
    {
        using var fixture = new Fixture();
        var pending = fixture.Prepare();
        fixture.Store.FaultPoint = phase => { if (phase == "prepared") throw new IOException("simulated disk error"); };
        Assert.Throws<IOException>(() => fixture.Prepare(pending.Id));
        Assert.Equal(pending.Id, fixture.Store.GetStatus().Id);
    }

    [Fact]
    public void ProcessLease_PreventsTwoApplicationInstancesUsingTheSameVolume()
    {
        using var fixture = new Fixture();
        using var first = fixture.Store.AcquireProcessLease();
        Assert.Throws<IOException>(() => new PendingRestoreStore(fixture.Root, "feedarr.db").AcquireProcessLease());
    }

    [Fact]
    public void FilesystemFailureAfterIntent_RetainsRescueAndCanResume()
    {
        using var fixture = new Fixture();
        fixture.CreateDatabase(fixture.Active, "original");
        var pending = fixture.Prepare();
        var blocked = Path.Combine(fixture.Root, "restore-state", "stages", pending.Id!, "install.db");
        fixture.Store.FaultPoint = phase => { if (phase == "journaled") Directory.CreateDirectory(blocked); };
        Assert.ThrowsAny<Exception>(() => fixture.Store.ApplyAtStartup());
        Assert.Equal("original", fixture.ReadMarker(fixture.Active));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "restore-state", "operation.json")));
        Assert.Throws<BackupOperationException>(() => fixture.Store.Cancel(pending.Id!));
        Directory.Delete(blocked);
        var restart = new PendingRestoreStore(fixture.Root, "feedarr.db");
        restart.ApplyAtStartup();
        Assert.Equal("selected backup", fixture.ReadMarker(fixture.Active));
    }

    [Fact]
    public void CorruptRescueOrIncompleteJournal_PreventsFurtherReplacement()
    {
        using var fixture = new Fixture();
        fixture.CreateDatabase(fixture.Active, "original");
        var pending = fixture.Prepare();
        fixture.Store.FaultPoint = phase => { if (phase == "journaled") throw new IOException("simulated crash"); };
        Assert.Throws<IOException>(() => fixture.Store.ApplyAtStartup());
        var rescue = Directory.GetFiles(Path.Combine(fixture.Root, "restore-state", "rescue"), "original.db", SearchOption.AllDirectories).Single();
        File.AppendAllText(rescue, "tampered rescue");
        fixture.Store.FaultPoint = null;
        Assert.Throws<InvalidDataException>(() => fixture.Store.ApplyAtStartup());
        Assert.Equal("original", fixture.ReadMarker(fixture.Active));
        File.WriteAllText(Path.Combine(fixture.Root, "restore-state", "operation.json"),
            System.Text.Json.JsonSerializer.Serialize(new { Version = 1, Id = pending.Id }));
        Assert.Throws<System.Text.Json.JsonException>(() => fixture.Store.ApplyAtStartup());
    }

    [Fact]
    public void SymlinkTampering_IsRejectedBeforeReadingOrInstallingItsTarget()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.CreateDatabase(fixture.Active, "original");
        var pending = fixture.Prepare();
        var incoming = Path.Combine(fixture.Root, "restore-state", "stages", pending.Id!, "incoming.db");
        File.Delete(incoming);
        File.CreateSymbolicLink(incoming, fixture.Active);
        Assert.Throws<InvalidDataException>(() => fixture.Store.ApplyAtStartup());
        Assert.Equal("original", fixture.ReadMarker(fixture.Active));
    }

    [Fact]
    public void PreparationCancellation_PreservesThePreviousPendingRestore()
    {
        using var fixture = new Fixture();
        var pending = fixture.Prepare();
        var work = fixture.Store.NewWorkDirectory();
        fixture.CreateDatabase(Path.Combine(work, "incoming.db"), "cancelled preparation");
        using var token = new CancellationTokenSource();
        token.Cancel();
        Assert.Throws<OperationCanceledException>(() => fixture.Store.Publish(work, "cancelled.zip", pending.Id, token.Token));
        Assert.Equal(pending.Id, fixture.Store.GetStatus().Id);
    }

    [Fact]
    public void InterruptedRescueCopy_IsRebuiltBeforePublishingAnyReplacementIntent()
    {
        using var fixture = new Fixture();
        fixture.CreateDatabase(fixture.Active, "original");
        var pending = fixture.Prepare();
        var rescue = Path.Combine(fixture.Root, "restore-state", "rescue", pending.Id!);
        Directory.CreateDirectory(rescue);
        File.WriteAllText(Path.Combine(rescue, "copying.db"), "interrupted incomplete SQLite backup");
        fixture.Store.ApplyAtStartup();
        Assert.Equal("original", fixture.ReadMarker(Path.Combine(rescue, "original.db")));
        Assert.Equal("selected backup", fixture.ReadMarker(fixture.Active));
        Assert.False(File.Exists(Path.Combine(rescue, "copying.db")));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "feedarr-restore-tests", Guid.NewGuid().ToString("N"));
        internal string Active => Path.Combine(Root, "feedarr.db");
        internal PendingRestoreStore Store { get; }
        internal Fixture() { Directory.CreateDirectory(Root); Store = new PendingRestoreStore(Root, "feedarr.db"); }
        internal void CreateDatabase(string path, string marker)
        {
            using var db = PendingRestoreStore.OpenDatabase(path, readOnly: false, create: true);
            db.Execute("""
                CREATE TABLE marker(value TEXT);
                INSERT INTO marker VALUES(@marker);
                CREATE TABLE releases(id INTEGER PRIMARY KEY,poster_file TEXT,poster_key TEXT,poster_store_dir TEXT);
                INSERT INTO releases VALUES(1,'kept.jpg',NULL,NULL),(2,'missing.jpg',NULL,NULL);
                """, new { marker });
        }
        internal string ReadMarker(string path) { using var db = PendingRestoreStore.OpenDatabase(path, true); return db.ExecuteScalar<string>("SELECT value FROM marker")!; }
        internal PendingRestoreStatus Prepare(string? replaceId = null)
        {
            var work = Store.NewWorkDirectory();
            CreateDatabase(Path.Combine(work, "incoming.db"), "selected backup");
            return Store.Publish(work, "selected.zip", replaceId, default);
        }
        internal void CreatePoster(string name, string value)
        { Directory.CreateDirectory(Path.Combine(Root, "posters")); File.WriteAllText(Path.Combine(Root, "posters", name), value); }
        public void Dispose() { Directory.Delete(Root, true); }
    }
}
