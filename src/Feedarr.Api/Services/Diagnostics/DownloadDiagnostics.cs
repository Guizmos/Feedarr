using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;

namespace Feedarr.Api.Services.Diagnostics;

public sealed record DownloadDiagnostic(string State, int? StatusCode = null, string? Code = null);

// Only opaque server-validated browser identities, states and codes; never torrent data.
public sealed class DownloadDiagnostics : IDisposable
{
    public const string FailureCodeKey = "feedarr.download.failure_code";
    private readonly MemoryCache _cache;
    private readonly object _gate = new();
    internal int EntryCount => _cache.Count;

    public DownloadDiagnostics(TimeProvider? timeProvider = null) => _cache = new(new MemoryCacheOptions
    {
        SizeLimit = 1024,
        Clock = new CacheClock(timeProvider ?? TimeProvider.System)
    });

    public bool TryStart(string session, long releaseId, Guid attempt)
    {
        lock (_gate)
        {
            var key = (session, releaseId, attempt);
            if (_cache.TryGetValue(key, out _)) return false;
            var pending = new DownloadDiagnostic("pending");
            Store(key, pending);
            return ReferenceEquals(_cache.Get<DownloadDiagnostic>(key), pending);
        }
    }

    public void Finish(string session, long releaseId, Guid attempt, int status, bool magnet, string? code)
    {
        lock (_gate)
        {
            var key = (session, releaseId, attempt);
            // An expired or completed attempt cannot be resurrected or overwritten.
            if (_cache.Get<DownloadDiagnostic>(key)?.State != "pending") return;
            Store(key, new(status is >= 200 and < 300 || magnet ? "completed" : "failed", status, code));
        }
    }

    public DownloadDiagnostic? Get(string session, long releaseId, Guid attempt) =>
        _cache.Get<DownloadDiagnostic>((session, releaseId, attempt));

    private void Store((string, long, Guid) key, DownloadDiagnostic value) =>
        _cache.Set(key, value, new MemoryCacheEntryOptions
        { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });

    public void Dispose() => _cache.Dispose();
    private sealed class CacheClock(TimeProvider timeProvider) : ISystemClock
    {
        public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
    }
}
