using System.Diagnostics;
using System.Security.Cryptography;
using Dapper;
using Feedarr.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Feedarr.Api.Tests;

public sealed partial class RetentionServiceTests
{
    private static readonly long HistoryOld = DateTimeOffset.UtcNow.AddDays(-60).ToUnixTimeSeconds();
    private static readonly HistoryCleanupRequest HistoryExecute = new(DryRun: false, Confirmation: "RECLAIM_UNUSED_HISTORY");

    private static long AddHistoryEntity(RetentionTestContext context, string title, string? poster = null, string origin = "automatic", bool recent = false)
    {
        using var conn = context.Db.Open();
        var id = conn.ExecuteScalar<long>("""
            INSERT INTO media_entities(unified_category,title_clean,year,created_at_ts,updated_at_ts,poster_file,retention_origin)
            VALUES('Film',@title,2024,@old,@old,@poster,@origin); SELECT last_insert_rowid();
            """, new { title, poster, origin, old = HistoryOld });
        conn.Execute("UPDATE media_entities SET orphaned_at_ts=@age,created_at_ts=@age,updated_at_ts=@age WHERE id=@id",
            new { id, age = recent ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : HistoryOld });
        return id;
    }
    private static string AddHistoryFile(RetentionTestContext context, string name, bool recent = false)
    {
        var path = context.CreatePosterFile(name);
        File.SetLastWriteTimeUtc(path, recent ? DateTime.UtcNow : DateTime.UtcNow.AddDays(-60));
        return path;
    }
    private static bool HistoryExists(RetentionTestContext context, long id)
    {
        using var conn = context.Db.Open();
        return conn.ExecuteScalar<long>("SELECT COUNT(*) FROM media_entities WHERE id=@id", new { id }) != 0;
    }
    private static Dictionary<string, string> HistorySnapshot(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), path =>
            File.GetLastWriteTimeUtc(path).Ticks + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    [Fact]
    public async Task HistoryCleanupCannotRunDuringConfigurationRestore()
    {
        var coordinator = new Feedarr.Api.Services.Backup.BackupExecutionCoordinator();
        using var c = new RetentionTestContext(coordinator: coordinator);
        var file = AddHistoryFile(c, "tmdb-106-w500.jpg");
        var id = AddHistoryEntity(c, "restore");
        await coordinator.RunExclusiveAsync("restore-configuration", null, async _ =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => c.Service.CleanupHistoryAsync(HistoryExecute));
            Assert.True(HistoryExists(c, id)); Assert.True(File.Exists(file));
            return true;
        });
    }

    [Fact]
    public async Task HistoryCleanup_OnlyProvenAutomaticUnreferencedEntitiesAfterGraceAreEligible()
    {
        using var c = new RetentionTestContext();
        var unused = AddHistoryEntity(c, "unused");
        var linked = AddHistoryEntity(c, "linked");
        var recent = AddHistoryEntity(c, "recent", recent: true);
        var legacy = AddHistoryEntity(c, "legacy", origin: "legacy_unknown");
        var manual = AddHistoryEntity(c, "manual", origin: "manual");
        var arr = AddHistoryEntity(c, "arr");
        var request = AddHistoryEntity(c, "request");
        var release = c.CreateRelease("linked", 100, "poster.jpg");
        using (var conn = c.Db.Open())
        {
            conn.Execute("UPDATE releases SET entity_id=@linked WHERE id=@release", new { linked, release });
            conn.Execute("INSERT INTO media_entity_arr_status(entity_id,checked_at_ts) VALUES(@arr,1)", new { arr });
            conn.Execute("UPDATE media_entities SET request_tmdb_id=123 WHERE id=@request", new { request });
        }
        var preview = await c.Service.CleanupHistoryAsync(new());
        Assert.Equal(1, preview.EntityCandidates);
        Assert.Equal(6, preview.ProtectedEntities);
        var real = await c.Service.CleanupHistoryAsync(HistoryExecute);
        Assert.Equal(1, real.EntitiesDeleted);
        Assert.False(HistoryExists(c, unused));
        foreach (var id in new[] { linked, recent, legacy, manual, arr, request }) Assert.True(HistoryExists(c, id));
    }

    [Fact]
    public async Task HistoryCleanup_ManualRenameProvenanceSurvivesLossOfRelease()
    {
        using var c = new RetentionTestContext();
        var release = c.CreateRelease("original", 100, "poster.jpg");
        c.Releases.RenameAndRebindEntity(release, "User corrected title 2024");
        using var conn = c.Db.Open();
        var entity = conn.ExecuteScalar<long>("SELECT entity_id FROM releases WHERE id=@release", new { release });
        Assert.Equal("manual", conn.ExecuteScalar<string>("SELECT retention_origin FROM media_entities WHERE id=@entity", new { entity }));
        conn.Execute("DELETE FROM releases WHERE id=@release; UPDATE media_entities SET orphaned_at_ts=@old,created_at_ts=@old,updated_at_ts=@old WHERE id=@entity", new { release, entity, old = HistoryOld });
        await c.Service.CleanupHistoryAsync(HistoryExecute);
        Assert.True(HistoryExists(c, entity));
    }

    [Fact]
    public async Task HistoryCleanup_NewDependencyAndUnknownPayloadProtectEntities()
    {
        using var c = new RetentionTestContext();
        var dependency = AddHistoryEntity(c, "bookmarked");
        var unknown = AddHistoryEntity(c, "unknown metadata");
        using (var conn = c.Db.Open())
        {
            conn.Execute("CREATE TABLE user_bookmarks(media_entity_id INTEGER REFERENCES media_entities(id)); INSERT INTO user_bookmarks VALUES(@dependency)", new { dependency });
            conn.Execute("ALTER TABLE media_entities ADD COLUMN future_user_note TEXT; UPDATE media_entities SET future_user_note='preserve' WHERE id=@unknown", new { unknown });
        }
        var result = await c.Service.CleanupHistoryAsync(HistoryExecute);
        Assert.Equal(0, result.EntitiesDeleted);
        Assert.Equal(1, result.ProtectionReasons["entity:other_dependency"]);
        Assert.Equal(1, result.ProtectionReasons["entity:unknown_data"]);
    }

    [Fact]
    public async Task HistoryCleanup_GraceResetsWhenAnOldEntityIsUsedAgain()
    {
        using var c = new RetentionTestContext();
        var id = AddHistoryEntity(c, "old reused");
        var release = c.CreateRelease("reuse", 100, "poster.jpg");
        using (var conn = c.Db.Open())
        {
            conn.Execute("UPDATE releases SET entity_id=@id WHERE id=@release", new { id, release });
            Assert.Null(conn.ExecuteScalar<long?>("SELECT orphaned_at_ts FROM media_entities WHERE id=@id", new { id }));
            conn.Execute("DELETE FROM releases WHERE id=@release", new { release });
            Assert.True(conn.ExecuteScalar<long>("SELECT orphaned_at_ts FROM media_entities WHERE id=@id", new { id }) > HistoryOld);
        }
        Assert.Equal(0, (await c.Service.CleanupHistoryAsync(new())).EntityCandidates);
    }

    [Fact]
    public async Task HistoryCleanup_ReclaimsFreedLegacyFileAndIsIdempotent()
    {
        using var c = new RetentionTestContext();
        var file = AddHistoryFile(c, "tmdb-100-w500.jpg");
        AddHistoryEntity(c, "unused", Path.GetFileName(file));
        var preview = await c.Service.CleanupHistoryAsync(new());
        Assert.Equal(1, preview.EntityCandidates);
        Assert.Equal(1, preview.PosterCandidates);
        Assert.Equal(4, preview.EstimatedRecoverableBytes);
        Assert.True(File.Exists(file));
        var real = await c.Service.CleanupHistoryAsync(HistoryExecute);
        Assert.Equal(1, real.EntitiesDeleted);
        Assert.Equal(1, real.PostersDeleted);
        Assert.Equal(4, real.ReclaimedBytes);
        Assert.Equal(0, real.MissingPosterFiles);
        Assert.False(File.Exists(file));
        var again = await c.Service.CleanupHistoryAsync(HistoryExecute);
        Assert.Equal(0, again.EntitiesDeleted);
        Assert.Equal(0, again.PostersDeleted);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("entity")]
    [InlineData("cache")]
    public async Task HistoryCleanup_SharedReferencesProtectFlatFiles(string reference)
    {
        using var c = new RetentionTestContext();
        const string name = "tmdb-101-w500.jpg";
        var path = AddHistoryFile(c, name);
        AddHistoryEntity(c, "candidate", name);
        using (var conn = c.Db.Open())
        {
            if (reference == "release") c.CreateRelease("shared", 100, name);
            if (reference == "entity") AddHistoryEntity(c, "protected legacy", name, "legacy_unknown");
            if (reference == "cache") conn.Execute("INSERT INTO poster_matches(fingerprint,poster_file,match_source,created_ts,last_seen_ts) VALUES('cache',@name,'tmdb',1,1)", new { name });
        }
        var result = await c.Service.CleanupHistoryAsync(HistoryExecute);
        Assert.Equal(1, result.EntitiesDeleted);
        Assert.Equal(0, result.PostersDeleted);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task HistoryCleanup_RevalidatesEntitiesAndFilesImmediatelyBeforeDeletion()
    {
        using var c = new RetentionTestContext();
        var entity = AddHistoryEntity(c, "candidate");
        const string name = "tmdb-102-w500.jpg";
        var file = AddHistoryFile(c, name);
        c.Service.HistoryFaultPoint = point =>
        {
            if (point == "before-entity-revalidation")
            {
                var release = c.CreateRelease("raced entity", 100, "other.jpg");
                using var conn = c.Db.Open();
                conn.Execute("UPDATE releases SET entity_id=@entity WHERE id=@release", new { entity, release });
            }
            if (point == "before-poster-revalidation") c.CreateRelease("raced poster", 100, name);
        };
        var result = await c.Service.CleanupHistoryAsync(HistoryExecute);
        Assert.Equal(0, result.EntitiesDeleted);
        Assert.Equal(0, result.PostersDeleted);
        Assert.True(HistoryExists(c, entity));
        Assert.True(File.Exists(file));
        Assert.Equal(1, result.ProtectionReasons["poster:referenced_before_delete"]);
    }

    [Fact]
    public async Task HistoryCleanup_ActivePublisherIsProtectedWithoutWaiting()
    {
        using var c = new RetentionTestContext();
        var file = AddHistoryFile(c, "tmdb-103-w500.jpg");
        var entity = AddHistoryEntity(c, "publishing");
        using var publication = await c.Releases.PosterLifecycle.EnterPublicationAsync(default);
        var result = await c.Service.CleanupHistoryAsync(HistoryExecute).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(result.PublicationBusy);
        Assert.Equal(0, result.EntitiesDeleted);
        Assert.Equal(0, result.PostersDeleted);
        Assert.True(File.Exists(file));
        Assert.True(HistoryExists(c, entity));
    }

    [Fact]
    public async Task HistoryCleanup_PublicationWaitsUntilTheShortGcLeaseEnds()
    {
        using var c = new RetentionTestContext();
        var release = c.CreateRelease("manual", 100, "poster.jpg");
        var lease = c.Releases.PosterLifecycle.TryEnterCollection();
        Assert.NotNull(lease);
        var publish = c.Posters.SaveRawgPosterAsync(release, 0, "unused", default);
        try { Assert.False(publish.IsCompleted); }
        finally { lease.Dispose(); }
        Assert.Null(await publish.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task HistoryCleanup_InterruptionAfterEntityCommitResumesSafely()
    {
        using var c = new RetentionTestContext();
        var file = AddHistoryFile(c, "tmdb-104-w500.jpg");
        var id = AddHistoryEntity(c, "crash", Path.GetFileName(file));
        c.Service.HistoryFaultPoint = point => { if (point == "before-poster-revalidation") throw new OperationCanceledException("interrupted"); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.Service.CleanupHistoryAsync(HistoryExecute));
        Assert.False(HistoryExists(c, id));
        Assert.True(File.Exists(file));
        using (var conn = c.Db.Open()) Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM activity_log WHERE event_type='history_cleanup'"));
        c.Service.HistoryFaultPoint = null;
        Assert.Equal(1, (await c.Service.CleanupHistoryAsync(HistoryExecute)).PostersDeleted);
        Assert.Equal(0, (await c.Service.CleanupHistoryAsync(HistoryExecute)).PostersDeleted);
    }

    [Fact]
    public async Task HistoryCleanup_DryRunChangesNoDatabaseOrFilesystemBytes()
    {
        using var c = new RetentionTestContext();
        var file = AddHistoryFile(c, "tmdb-105-w500.jpg");
        AddHistoryEntity(c, "dryrun", Path.GetFileName(file));
        var root = Path.GetDirectoryName(c.Db.DbPath)!;
        var before = HistorySnapshot(root);
        var controller = c.CreateMaintenanceController();
        var result = Assert.IsType<OkObjectResult>(await controller.PreviewHistoryCleanup(c.Service, default));
        var report = Assert.IsType<HistoryCleanupReport>(result.Value);
        Assert.True(report.DryRun);
        Assert.Equal(1, report.EntityCandidates);
        Assert.Equal(1, report.PosterCandidates);
        Assert.Equal(4, report.EstimatedRecoverableBytes);
        Assert.Equal(before.OrderBy(row => row.Key), HistorySnapshot(root).OrderBy(row => row.Key));
        Assert.IsType<BadRequestObjectResult>(await controller.HistoryCleanup(c.Service, new(DryRun: false), default));
        Assert.Equal(before.OrderBy(row => row.Key), HistorySnapshot(root).OrderBy(row => row.Key));
    }

    [Fact]
    public async Task HistoryCleanup_LargeCatalogIsBoundedAndPaged()
    {
        using var c = new RetentionTestContext();
        using (var conn = c.Db.Open())
        {
            conn.Execute("""
                WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<100000)
                INSERT INTO media_entities(unified_category,title_clean,created_at_ts,updated_at_ts,retention_origin)
                SELECT 'Film','auto-'||x,@old,@old,'automatic' FROM n;
                UPDATE media_entities SET orphaned_at_ts=@old;
                """, new { old = HistoryOld });
        }
        for (var i = 0; i < 50; i++) AddHistoryFile(c, $"tmdb-{100000 + i}-w500.jpg");
        var timer = Stopwatch.StartNew();
        var first = await c.Service.CleanupHistoryAsync(HistoryExecute with { Limit = 25, ScanLimit = 30 });
        Assert.Equal(100000, first.EntityCandidates);
        Assert.Equal(25, first.EntitiesDeleted);
        Assert.Equal(25, first.PostersDeleted);
        Assert.Equal(30, first.PostersScanned);
        Assert.NotNull(first.NextPoster);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30));
        var next = await c.Service.CleanupHistoryAsync(new(ScanLimit: 30, AfterPoster: first.NextPoster));
        Assert.Equal(20, next.PostersScanned);
        Assert.Null(next.NextPoster);
    }





    [Fact]
    public async Task HistoryCleanup_UnknownRecentAndSymlinkFilesStayProtectedAndMissingStoresAreNotRepaired()
    {
        using var c = new RetentionTestContext();
        var unknown = AddHistoryFile(c, "family-photo.jpg");
        var manual = AddHistoryFile(c, "tmdb-107-manual.jpg");
        var recent = AddHistoryFile(c, "tmdb-108-w500.jpg", recent: true);
        var link = Path.Combine(c.Posters.PostersDirPath, "tmdb-109-w500.jpg");
        File.CreateSymbolicLink(link, unknown);
        var entity = AddHistoryEntity(c, "missing store", origin: "legacy_unknown");
        using (var conn = c.Db.Open()) conn.Execute("UPDATE media_entities SET poster_store_dir='tmdb-no-physical-directory' WHERE id=@entity", new { entity });
        var result = await c.Service.CleanupHistoryAsync(HistoryExecute);
        Assert.Equal(0, result.PostersDeleted);
        Assert.Equal(1, result.MissingStoreDirectories);
        Assert.Equal(1, result.MissingStoreReferenceRows);
        foreach (var path in new[] { unknown, manual, recent, link }) Assert.True(File.Exists(path));
    }

    [Fact]
    public void HistoryCleanup_RebindTracksAutomaticAndManualOriginsWithoutAdoptingLegacyRows()
    {
        using var c = new RetentionTestContext();
        var automatic = c.CreateRelease("automatic", 100, "auto.jpg");
        var manual = c.CreateRelease("manual", 100, "manual.jpg");
        var legacyEntity = AddHistoryEntity(c, "legacy-stays", origin: "legacy_unknown");
        var legacyRelease = c.CreateRelease("legacy-stays", 100, "legacy.jpg");
        using var conn = c.Db.Open();
        conn.Execute("UPDATE releases SET needs_rebind=1; UPDATE releases SET title_manual_override=1 WHERE id=@manual", new { manual });
        c.Releases.RebindEntitiesForSource(c.SourceId);
        string? Origin(long release) => conn.ExecuteScalar<string>("SELECT retention_origin FROM media_entities WHERE id=(SELECT entity_id FROM releases WHERE id=@release)", new { release });
        Assert.Equal("automatic", Origin(automatic));
        Assert.Equal("manual", Origin(manual));
        Assert.Equal("legacy_unknown", Origin(legacyRelease));
        Assert.True(HistoryExists(c, legacyEntity));
    }

    [Fact]
    public async Task HistoryCleanup_ManualCacheAndManualFileProtectAutomaticEntities()
    {
        using var c = new RetentionTestContext();
        var file = AddHistoryEntity(c, "manual file", "tmdb-111-manual.jpg");
        var cached = AddHistoryEntity(c, "manual cache");
        using (var conn = c.Db.Open())
            conn.Execute("INSERT INTO poster_matches(fingerprint,normalized_title,year,match_source,created_ts,last_seen_ts) VALUES('manual','manual cache',2024,'manual',1,1)");
        var report = await c.Service.CleanupHistoryAsync(HistoryExecute);
        Assert.Equal(2, report.ProtectionReasons["entity:manual_metadata"]);
        Assert.Equal(0, report.EntitiesDeleted);
        Assert.True(HistoryExists(c, file));
        Assert.True(HistoryExists(c, cached));
    }

    [Fact]
    public async Task HistoryCleanup_FailedDeleteAttemptsAreAlsoBounded()
    {
        using var c = new RetentionTestContext(new ThrowingPosterFileStore());
        for (var i = 0; i < 50; i++) AddHistoryFile(c, $"tmdb-{100000 + i}-w500.jpg");
        var result = await c.Service.CleanupHistoryAsync(HistoryExecute with { Limit = 3 });
        Assert.Equal(50, result.PosterCandidates);
        Assert.Equal(3, result.PosterDeleteAttempts);
        Assert.Equal(3, result.Errors);
        Assert.Equal(0, result.PostersDeleted);
    }

    [Fact]
    public async Task HistoryCleanup_FileDeletionErrorsAreReportedAndCanBeRetried()
    {
        using var c = new RetentionTestContext(new ThrowingPosterFileStore());
        var file = AddHistoryFile(c, "tmdb-110-w500.jpg");
        var result = await c.Service.CleanupHistoryAsync(HistoryExecute);
        Assert.Equal(1, result.Errors);
        Assert.Equal(0, result.PostersDeleted);
        Assert.Equal(0, result.ReclaimedBytes);
        Assert.True(File.Exists(file));
    }
}
