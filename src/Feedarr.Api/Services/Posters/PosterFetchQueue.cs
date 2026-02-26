using System.Threading.Channels;
using System.Collections.Concurrent;

namespace Feedarr.Api.Services.Posters;

public sealed class PosterFetchQueue : IPosterFetchQueue
{
    // 2 000 slots: each PosterFetchJob is a lightweight record (a few fields).
    // At ~200 bytes/job this is ~400 KB worst case — well within acceptable bounds.
    // Chosen rationale:
    //   - A typical library scan queues at most a few hundred new items at once.
    //   - 2 000 gives ample headroom for large libraries without unbounded growth.
    //   - DropWrite policy: new arrivals are silently dropped when full; the
    //     deduplication set (_pendingByItemId) prevents re-queuing until the
    //     existing job is consumed, so progress is never blocked.
    private const int Capacity = 2000;

    private readonly Channel<PosterFetchJob> _channel;
    private readonly ConcurrentDictionary<long, byte> _pendingByItemId = new();
    private readonly ILogger<PosterFetchQueue> _log;
    // Guards the TryAdd-then-TryWrite pair so that two concurrent callers
    // for the same ItemId cannot both observe "not pending" before either
    // has written to the channel, which would cause one to return true
    // incorrectly while the job gets dropped.
    private readonly object _enqueueLock = new();

    public PosterFetchQueue(ILogger<PosterFetchQueue> log)
    {
        _log = log;
        var options = new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            // DropWrite: prefer dropping new arrivals over blocking the producer.
            // The poster worker will eventually drain the queue and re-trigger
            // on the next sync cycle for any dropped items.
            FullMode = BoundedChannelFullMode.DropWrite,
        };
        _channel = Channel.CreateBounded<PosterFetchJob>(options);
    }

    /// <summary>Current number of jobs pending in the queue.</summary>
    public int Count => (int)(_channel.Reader.Count);

    public bool TryEnqueue(PosterFetchJob job)
    {
        if (job.ItemId <= 0)
            return false;

        // The lock makes the ContainsKey check and TryWrite atomic so that
        // two concurrent callers for the same ItemId cannot both pass the
        // "not yet pending" guard before either has written to the channel.
        lock (_enqueueLock)
        {
            // Deduplicate: if already tracked (in channel), accept without re-adding.
            if (_pendingByItemId.ContainsKey(job.ItemId))
                return true;

            if (!_channel.Writer.TryWrite(job))
            {
                _log.LogWarning(
                    "PosterFetchQueue is full (capacity={Capacity}). Dropped job for ItemId={ItemId}. " +
                    "The item will be retried on the next sync cycle.",
                    Capacity, job.ItemId);
                return false;
            }

            _pendingByItemId.TryAdd(job.ItemId, 0);
            return true;
        }
    }

    public async ValueTask<PosterFetchJob> DequeueAsync(CancellationToken ct)
    {
        var job = await _channel.Reader.ReadAsync(ct);
        _pendingByItemId.TryRemove(job.ItemId, out _);
        return job;
    }

    public int ClearPending()
    {
        var cleared = 0;
        while (_channel.Reader.TryRead(out var job))
        {
            cleared++;
            _pendingByItemId.TryRemove(job.ItemId, out _);
        }

        if (cleared > 0)
            _log.LogInformation("PosterFetchQueue cleared {Cleared} pending jobs", cleared);

        return cleared;
    }
}
