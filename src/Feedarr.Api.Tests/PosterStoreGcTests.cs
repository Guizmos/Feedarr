using Dapper;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Posters;
using Microsoft.Data.Sqlite;

namespace Feedarr.Api.Tests;

public sealed partial class RetentionServiceTests
{
    [Theory]
    [InlineData(false, "retention")]
    [InlineData(true, "retention")]
    [InlineData(false, "maintenance")]
    [InlineData(true, "maintenance")]
    [InlineData(false, "replacement")]
    [InlineData(true, "replacement")]
    public async Task StoreGc_ProtectsEntityOnlyStore(bool linked, string collector)
    {
        using var context = new RetentionTestContext();
        const string store = "rawg-entity";
        var path = context.CreateStoreDirectory(store);
        var entityId = CreatePosterEntity(context, store);
        if (linked)
        {
            var id = context.CreateRelease("linked", 100, "entity.jpg");
            using var conn = context.Db.Open();
            conn.Execute("UPDATE releases SET entity_id=@entityId,poster_store_dir=NULL WHERE id=@id", new { id, entityId });
        }
        Assert.Empty(context.Releases.GetOrphanedStoreDirs([store]));

        switch (collector)
        {
            case "retention": context.Service.ApplyRetention(context.SourceId, 0, 0); break;
            case "maintenance": context.CreateMaintenanceController().CleanupPosters(); break;
            case "replacement":
                var replaced = context.CreateRelease("replaced", 200, "replace.png", store);
                await MaterializeExistingPoster(context, replaced, "new");
                break;
        }
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void StoreGc_ProtectsSharedDirectReferencesWithoutRefcountRows()
    {
        using var context = new RetentionTestContext();
        var path = context.CreateStoreDirectory("rawg-shared");
        context.CreateRelease("one", 100, "one.jpg", "rawg-shared");
        context.CreateRelease("two", 200, "two.jpg", "rawg-shared");
        CreatePosterEntity(context, "rawg-shared");
        CreatePosterEntity(context, "rawg-shared");
        using (var conn = context.Db.Open()) conn.Execute("DELETE FROM poster_store_refs");
        Assert.Empty(context.Releases.GetOrphanedStoreDirs(["rawg-shared"]));
        Assert.False(context.Posters.CleanupOrphanedStoreDir("rawg-shared").Deleted);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void StoreGc_DeletesActualOrphanAndMeasuresBytes()
    {
        using var context = new RetentionTestContext();
        var path = context.CreateStoreDirectory("rawg-orphan");
        Assert.Equal(["rawg-orphan"], context.Releases.GetOrphanedStoreDirs(["rawg-orphan"]));
        var result = context.Posters.CleanupOrphanedStoreDir("rawg-orphan", measureBytes: true);
        Assert.True(result.Deleted);
        Assert.False(result.Failed);
        Assert.Equal(6, result.FreedBytes);
        Assert.False(Directory.Exists(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreGc_WaitsForPublicationAndRevalidatesOldCandidate(bool entityOnly)
    {
        using var context = new RetentionTestContext();
        const string store = "rawg-publishing";
        var path = context.CreateStoreDirectory(store);
        Assert.Single(context.Releases.GetOrphanedStoreDirs([store]));
        var storeLock = context.Posters.GetStoreLock(store);
        await storeLock.WaitAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gc = Task.Run(() => { started.SetResult(); return context.Posters.CleanupOrphanedStoreDir(store); });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(gc.IsCompleted);
            if (entityOnly) CreatePosterEntity(context, store);
            else
            {
                var id = context.CreateRelease("published", 100, "published.jpg");
                context.Releases.SavePosterWithStore(id, null, null, "published.jpg", "rawg:publishing", store);
            }
        }
        finally { storeLock.Release(); }
        var result = await gc.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Deleted);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task StoreGc_DoesNotBlockUnrelatedStore()
    {
        using var context = new RetentionTestContext();
        context.CreateStoreDirectory("rawg-other");
        var held = context.Posters.GetStoreLock("rawg-busy");
        await held.WaitAsync();
        try
        {
            var result = await Task.Run(() => context.Posters.CleanupOrphanedStoreDir("rawg-other"))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(result.Deleted);
        }
        finally { held.Release(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreGc_CancellationDoesNotDeleteOrLeakLock(bool waiting)
    {
        using var context = new RetentionTestContext();
        var path = context.CreateStoreDirectory("rawg-cancel");
        var storeLock = context.Posters.GetStoreLock("rawg-cancel");
        using var cancel = new CancellationTokenSource();
        if (waiting) await storeLock.WaitAsync();
        else cancel.Cancel();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gc = Task.Run(() =>
        {
            started.SetResult();
            return context.Posters.CleanupOrphanedStoreDir("rawg-cancel", ct: cancel.Token);
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await gc.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { if (waiting) storeLock.Release(); }
        Assert.True(Directory.Exists(path));
        Assert.True(context.Posters.CleanupOrphanedStoreDir("rawg-cancel").Deleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoreGc_DeleteFailureRetainsReferencesAndReleasesLock(bool silentFailure)
    {
        var files = new FailingStoreDirectory(silentFailure);
        using var context = new RetentionTestContext(files);
        var path = context.CreateStoreDirectory("rawg-failed");
        var failed = context.Posters.CleanupOrphanedStoreDir("rawg-failed");
        Assert.True(failed.Failed);
        Assert.False(failed.Deleted);
        Assert.True(Directory.Exists(path));
        files.Fail = false;
        Assert.True(context.Posters.CleanupOrphanedStoreDir("rawg-failed").Deleted);
    }

    [Fact]
    public async Task StoreGc_ReplacementDeletesOnlyDisplacedOrphan()
    {
        using var context = new RetentionTestContext();
        var old = context.CreateStoreDirectory("rawg-old");
        var id = context.CreateRelease("replaced", 100, "replace.png", "rawg-old");
        await MaterializeExistingPoster(context, id, "new");
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(Path.Combine(context.Posters.PosterStoreDirPath, "rawg-new")));
        Assert.Equal("rawg-new", context.Releases.GetForPoster(id)!.PosterStoreDir);
    }

    [Fact]
    public async Task StoreGc_CrossedReplacementsNeverHoldTwoStoreLocks()
    {
        var queue = new PairedWarmupQueue();
        using var context = new RetentionTestContext(thumbQueue: queue);
        context.CreateStoreDirectory("rawg-a");
        context.CreateStoreDirectory("rawg-b");
        var a = context.CreateRelease("a", 100, "a.png", "rawg-a");
        var b = context.CreateRelease("b", 200, "b.png", "rawg-b");
        await Task.WhenAll(MaterializeExistingPoster(context, a, "b"), MaterializeExistingPoster(context, b, "a"))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("rawg-b", context.Releases.GetForPoster(a)!.PosterStoreDir);
        Assert.Equal("rawg-a", context.Releases.GetForPoster(b)!.PosterStoreDir);
        Assert.True(Directory.Exists(Path.Combine(context.Posters.PosterStoreDirPath, "rawg-a")));
        Assert.True(Directory.Exists(Path.Combine(context.Posters.PosterStoreDirPath, "rawg-b")));
    }



    [Fact]
    public async Task StoreGc_PublisherRecreatesStoreAfterConcurrentDeletion()
    {
        var files = new BlockingStoreDirectory();
        using var context = new RetentionTestContext(files);
        context.CreateStoreDirectory("rawg-new");
        var id = context.CreateRelease("publisher", 100, "publisher.png");
        var gc = Task.Run(() => context.Posters.CleanupOrphanedStoreDir("rawg-new"));
        Task? publish = null;
        try
        {
            await files.Deleting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            publish = MaterializeExistingPoster(context, id, "new");
            Assert.False(publish.IsCompleted);
        }
        finally { files.AllowDelete.TrySetResult(); }
        Assert.True((await gc.WaitAsync(TimeSpan.FromSeconds(5))).Deleted);
        await publish!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("rawg-new", context.Releases.GetForPoster(id)!.PosterStoreDir);
        Assert.True(File.Exists(Path.Combine(context.Posters.PosterStoreDirPath, "rawg-new", "original.png")));
        Assert.Empty(context.Releases.GetOrphanedStoreDirs(["rawg-new"]));
    }

    [Fact]
    public async Task StoreGc_MigrationSharesPublicationLock()
    {
        using var context = new RetentionTestContext();
        var id = context.CreateRelease("migration", 100, "migrate.png");
        context.CreatePosterFile("migrate.png");
        File.WriteAllBytes(Path.Combine(context.Posters.PostersDirPath, "migrate.png"), PosterTestImage.Create());
        using (var conn = context.Db.Open())
            conn.Execute("UPDATE releases SET poster_provider='rawg',poster_provider_id='migration' WHERE id=@id", new { id });
        var storeLock = context.Posters.GetStoreLock("rawg-migration");
        await storeLock.WaitAsync();
        Task<Microsoft.AspNetCore.Mvc.IActionResult> migration;
        try
        {
            migration = context.CreateMaintenanceController().MigratePosterStore();
            Assert.False(migration.IsCompleted);
            Assert.False(Directory.Exists(Path.Combine(context.Posters.PosterStoreDirPath, "rawg-migration")));
        }
        finally { storeLock.Release(); }
        await migration.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("rawg-migration", context.Releases.GetForPoster(id)!.PosterStoreDir);
        Assert.False(context.Posters.CleanupOrphanedStoreDir("rawg-migration").Deleted);
    }

    private sealed class BlockingStoreDirectory : IPosterFileStore
    {
        public TaskCompletionSource Deleting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDelete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Exists(string path) => File.Exists(path);
        public void Delete(string path) => File.Delete(path);
        public void DeleteDirectory(string path)
        {
            Deleting.TrySetResult();
            AllowDelete.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Directory.Delete(path, true);
        }
    }

    private static long CreatePosterEntity(RetentionTestContext context, string store)
    {
        using var conn = context.Db.Open();
        return conn.ExecuteScalar<long>("""
            INSERT INTO media_entities(unified_category,title_clean,created_at_ts,updated_at_ts,poster_file,poster_store_dir)
            VALUES('Film',@title,1,1,'entity.jpg',@store); SELECT last_insert_rowid();
            """, new { title = Guid.NewGuid().ToString("N"), store });
    }

    private static async Task MaterializeExistingPoster(RetentionTestContext context, long id, string providerId)
    {
        var row = context.Releases.GetForPoster(id)!;
        context.CreatePosterFile(row.PosterFile!);
        File.WriteAllBytes(Path.Combine(context.Posters.PostersDirPath, row.PosterFile!), PosterTestImage.Create());
        using (var conn = context.Db.Open())
            conn.Execute("UPDATE releases SET poster_provider='rawg',poster_provider_id=@providerId WHERE id=@id", new { id, providerId });
        var result = await context.Posters.FetchPosterAsync(id, default, logSingle: false);
        Assert.True(result.Ok);
    }

    private sealed class FailingStoreDirectory(bool silentFailure) : IPosterFileStore
    {
        public bool Fail { get; set; } = true;
        public bool Exists(string path) => File.Exists(path);
        public void Delete(string path) => File.Delete(path);
        public void DeleteDirectory(string path)
        {
            if (!Fail) Directory.Delete(path, true);
            else if (!silentFailure) throw new IOException("simulated store delete failure");
        }
    }

    private sealed class PairedWarmupQueue : IPosterThumbQueue
    {
        private readonly TaskCompletionSource _paired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public int Count => 0;
        public async ValueTask<PosterThumbEnqueueResult> EnqueueAsync(PosterThumbJob job, CancellationToken ct, TimeSpan timeout)
        {
            if (Interlocked.Increment(ref _arrivals) == 2) _paired.TrySetResult();
            await _paired.Task.WaitAsync(ct);
            return new(PosterThumbEnqueueStatus.Rejected);
        }
        public ValueTask<PosterThumbJob> DequeueAsync(CancellationToken ct) => ValueTask.FromCanceled<PosterThumbJob>(ct);
        public PosterThumbJob? Complete(PosterThumbJob job) => null;
    }
}
