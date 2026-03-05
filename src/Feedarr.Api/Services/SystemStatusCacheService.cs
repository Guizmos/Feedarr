using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Services;

public sealed record SystemStatusSnapshot(
    int SourcesCount,
    int ReleasesCount,
    long? ReleasesLatestTs,
    long? LastSyncAtTs,
    double DbSizeMb);

public interface ISystemStatusSnapshotProvider
{
    Task<SystemStatusSnapshot> LoadAsync(CancellationToken ct);
}

public sealed class SystemStatusSnapshotProvider : ISystemStatusSnapshotProvider
{
    private readonly Db _db;
    private readonly ILogger<SystemStatusSnapshotProvider> _log;

    public SystemStatusSnapshotProvider(Db db, ILogger<SystemStatusSnapshotProvider> log)
    {
        _db = db;
        _log = log;
    }

    public Task<SystemStatusSnapshot> LoadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.Open();
        using var multi = conn.QueryMultiple(
            """
            SELECT COUNT(1) FROM sources;
            SELECT COUNT(1) FROM releases;
            SELECT MAX(created_at_ts) FROM releases;
            SELECT MAX(last_sync_at_ts) FROM sources;
            """);

        var sourcesCount = multi.ReadSingle<int>();
        var releasesCount = multi.ReadSingle<int>();
        var releasesLatestTs = multi.ReadSingleOrDefault<long?>();
        var lastSyncAtTs = multi.ReadSingleOrDefault<long?>();

        var dbSizeMb = 0.0;
        try
        {
            if (File.Exists(_db.DbPath))
            {
                var bytes = new FileInfo(_db.DbPath).Length;
                dbSizeMb = Math.Round(bytes / 1024d / 1024d, 1);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read database size for system status snapshot");
        }

        return Task.FromResult(new SystemStatusSnapshot(
            sourcesCount,
            releasesCount,
            releasesLatestTs,
            lastSyncAtTs,
            dbSizeMb));
    }
}

public sealed class SystemStatusCacheService
{
    private const string CacheKey = "system:status:v1";
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(7);

    private readonly IMemoryCache _cache;
    private readonly ISystemStatusSnapshotProvider _provider;
    private readonly ILogger<SystemStatusCacheService> _log;
    private readonly TimeSpan _ttl;
    private readonly object _inflightLock = new();
    private Task<SystemStatusSnapshot>? _inflightLoad;

    public SystemStatusCacheService(
        IMemoryCache cache,
        ISystemStatusSnapshotProvider provider,
        IOptions<AppOptions> options,
        ILogger<SystemStatusCacheService> log)
    {
        _cache = cache;
        _provider = provider;
        _log = log;

        var configuredSeconds = options?.Value?.SystemStatusCacheSeconds ?? 7;
        _ttl = configuredSeconds > 0
            ? TimeSpan.FromSeconds(Math.Clamp(configuredSeconds, 1, 30))
            : DefaultTtl;
    }

    public async Task<SystemStatusSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue<SystemStatusSnapshot>(CacheKey, out var cached) && cached is not null)
        {
            _log.LogDebug("SystemStatus cache hit");
            return cached;
        }

        Task<SystemStatusSnapshot> loadTask;
        lock (_inflightLock)
        {
            if (_inflightLoad is not null)
            {
                _log.LogDebug("SystemStatus cache miss with in-flight load");
                loadTask = _inflightLoad;
            }
            else
            {
                _log.LogDebug("SystemStatus cache miss, loading snapshot");
                _inflightLoad = LoadAndCacheAsync();
                loadTask = _inflightLoad;
            }
        }

        return await loadTask.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<SystemStatusSnapshot> LoadAndCacheAsync()
    {
        try
        {
            var loaded = await _provider.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            _cache.Set(CacheKey, loaded, _ttl);
            return loaded;
        }
        finally
        {
            lock (_inflightLock)
            {
                _inflightLoad = null;
            }
        }
    }
}
