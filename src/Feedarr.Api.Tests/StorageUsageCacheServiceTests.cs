using Feedarr.Api.Data;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class StorageUsageCacheServiceTests
{
    [Fact]
    public async Task ConcurrentCalls_ShareSingleInflightScan()
    {
        using var workspace = new TestWorkspace();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var lifetime = new TestHostApplicationLifetime();

        var scanCount = 0;
        var service = CreateService(
            workspace,
            cache,
            lifetime,
            async ct =>
            {
                Interlocked.Increment(ref scanCount);
                await Task.Delay(100, ct);
                return new StorageUsageSnapshot(10, 1, 1, 20, 0, 0);
            });

        var first = service.GetSnapshotAsync();
        var second = service.GetSnapshotAsync();

        await Task.WhenAll(first, second);

        Assert.Equal(1, scanCount);
        Assert.Equal(10, first.Result.DatabaseBytes);
        Assert.Equal(10, second.Result.DatabaseBytes);
    }

    [Fact]
    public async Task FreshCache_ReturnsCachedSnapshotWithoutRescan()
    {
        using var workspace = new TestWorkspace();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var lifetime = new TestHostApplicationLifetime();

        var scanCount = 0;
        var service = CreateService(
            workspace,
            cache,
            lifetime,
            ct =>
            {
                Interlocked.Increment(ref scanCount);
                return Task.FromResult(new StorageUsageSnapshot(42, 2, 2, 84, 1, 21));
            });

        var first = await service.GetSnapshotAsync();
        var second = await service.GetSnapshotAsync();

        Assert.Equal(1, scanCount);
        Assert.Equal(first, second);
    }

    // -------------------------------------------------------------------------
    // Fix 5: single-scan correctness — top-level vs recursive counts
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ComputeSnapshot_SingleScan_CountsTopLevelAndRecursiveCorrectly()
    {
        using var workspace = new TestWorkspace();

        // posters/
        //   a.jpg   (top-level, 1 byte)
        //   b.jpg   (top-level, 2 bytes)
        //   sub/
        //     c.jpg (recursive-only, 3 bytes)
        var postersDir = Path.Combine(workspace.DataDir, "posters");
        var subDir = Path.Combine(postersDir, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllBytes(Path.Combine(postersDir, "a.jpg"), new byte[1]);
        File.WriteAllBytes(Path.Combine(postersDir, "b.jpg"), new byte[2]);
        File.WriteAllBytes(Path.Combine(subDir, "c.jpg"), new byte[3]);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var lifetime = new TestHostApplicationLifetime();

        var options = OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db",
            StorageUsageCacheTtlSeconds = 60
        });

        // Use the public constructor (no scanOverride → exercises real ComputeSnapshotAsync)
        var service = new StorageUsageCacheService(
            cache,
            new TestWebHostEnvironment(workspace.RootDir),
            options,
            new Db(options),
            lifetime,
            NullLogger<StorageUsageCacheService>.Instance);

        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal(2, snapshot.PostersTopLevelCount);  // a.jpg + b.jpg only
        Assert.Equal(3, snapshot.PostersRecursiveCount); // a.jpg + b.jpg + c.jpg
        Assert.Equal(1 + 2 + 3, snapshot.PostersBytes);  // 6 bytes total
    }

    private static StorageUsageCacheService CreateService(
        TestWorkspace workspace,
        IMemoryCache cache,
        TestHostApplicationLifetime lifetime,
        Func<CancellationToken, Task<StorageUsageSnapshot>> scanOverride)
    {
        var options = OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db",
            StorageUsageCacheTtlSeconds = 60
        });

        return new StorageUsageCacheService(
            cache,
            new TestWebHostEnvironment(workspace.RootDir),
            options,
            new Db(options),
            lifetime,
            NullLogger<StorageUsageCacheService>.Instance,
            scanOverride);
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public TestWebHostEnvironment(string rootDir)
        {
            ApplicationName = "Feedarr.Api.Tests";
            EnvironmentName = "Test";
            ContentRootPath = rootDir;
            ContentRootFileProvider = new NullFileProvider();
            WebRootPath = rootDir;
            WebRootFileProvider = new NullFileProvider();
        }

        public string ApplicationName { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public string EnvironmentName { get; set; }
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            RootDir = Path.Combine(Path.GetTempPath(), "feedarr-tests", Guid.NewGuid().ToString("N"));
            DataDir = Path.Combine(RootDir, "data");
            Directory.CreateDirectory(DataDir);
        }

        public string RootDir { get; }
        public string DataDir { get; }

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
