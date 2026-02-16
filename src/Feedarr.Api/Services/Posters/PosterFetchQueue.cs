using System.Threading.Channels;
using System.Collections.Concurrent;

namespace Feedarr.Api.Services.Posters;

public sealed class PosterFetchQueue : IPosterFetchQueue
{
    // Safety guard to avoid unbounded memory growth when many poster refreshes are queued.
    private const int MaxQueueSize = 2000;
    private readonly Channel<PosterFetchJob> _channel;
    private readonly ConcurrentDictionary<long, byte> _pendingByItemId = new();
    private int _count;

    public PosterFetchQueue()
    {
        var options = new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };
        _channel = Channel.CreateUnbounded<PosterFetchJob>(options);
        _count = 0;
    }

    public int Count => _count;

    public bool Enqueue(PosterFetchJob job)
    {
        if (job.ItemId <= 0)
            return false;

        // Deduplicate by release id: if already queued, consider it accepted.
        if (!_pendingByItemId.TryAdd(job.ItemId, 0))
            return true;

        if (Volatile.Read(ref _count) >= MaxQueueSize)
        {
            _pendingByItemId.TryRemove(job.ItemId, out _);
            return false;
        }

        var result = _channel.Writer.TryWrite(job);
        if (result)
            Interlocked.Increment(ref _count);
        else
            _pendingByItemId.TryRemove(job.ItemId, out _);
        return result;
    }

    public async ValueTask<PosterFetchJob> DequeueAsync(CancellationToken ct)
    {
        var job = await _channel.Reader.ReadAsync(ct);
        Interlocked.Decrement(ref _count);
        _pendingByItemId.TryRemove(job.ItemId, out _);
        return job;
    }

    public int ClearPending()
    {
        var cleared = 0;
        while (_channel.Reader.TryRead(out var job))
        {
            cleared++;
            Interlocked.Decrement(ref _count);
            _pendingByItemId.TryRemove(job.ItemId, out _);
        }
        return cleared;
    }
}
