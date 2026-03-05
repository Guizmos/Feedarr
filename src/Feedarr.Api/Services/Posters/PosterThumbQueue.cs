using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Feedarr.Api.Services.Posters;

public sealed class PosterThumbQueue : IPosterThumbQueue
{
    private const int Capacity = 2000;
    public const int DefaultEnqueueTimeoutMs = 2000;
    public static readonly TimeSpan DefaultEnqueueTimeout = TimeSpan.FromMilliseconds(DefaultEnqueueTimeoutMs);

    private readonly Channel<string> _channel;
    private readonly ConcurrentDictionary<string, QueueEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public PosterThumbQueue()
    {
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public int Count => (int)_channel.Reader.Count;

    public async ValueTask<PosterThumbEnqueueResult> EnqueueAsync(PosterThumbJob job, CancellationToken ct, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(job.StoreDir))
            return new PosterThumbEnqueueResult(PosterThumbEnqueueStatus.Rejected);

        var hasTimeout = timeout > TimeSpan.Zero;
        var deadlineUtc = hasTimeout ? DateTimeOffset.UtcNow + timeout : DateTimeOffset.UtcNow;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            lock (_gate)
            {
                if (TryCoalesceLocked(job, out var coalesced))
                    return coalesced;

                if (_channel.Writer.TryWrite(job.StoreDir))
                {
                    _entries[job.StoreDir] = new QueueEntry(job) { Pending = true };
                    return new PosterThumbEnqueueResult(PosterThumbEnqueueStatus.Enqueued);
                }
            }

            if (!hasTimeout || DateTimeOffset.UtcNow >= deadlineUtc)
                return new PosterThumbEnqueueResult(PosterThumbEnqueueStatus.TimedOut);

            var remaining = deadlineUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return new PosterThumbEnqueueResult(PosterThumbEnqueueStatus.TimedOut);

            using var timeoutCts = new CancellationTokenSource(remaining);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                var canWrite = await _channel.Writer.WaitToWriteAsync(linkedCts.Token).ConfigureAwait(false);
                if (!canWrite)
                    return new PosterThumbEnqueueResult(PosterThumbEnqueueStatus.TimedOut);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new PosterThumbEnqueueResult(PosterThumbEnqueueStatus.TimedOut);
            }
        }
    }

    public async ValueTask<PosterThumbJob> DequeueAsync(CancellationToken ct)
    {
        while (true)
        {
            var storeDir = await _channel.Reader.ReadAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (!_entries.TryGetValue(storeDir, out var entry) || !entry.Pending)
                    continue;

                entry.Pending = false;
                entry.InFlight = true;
                return entry.Job;
            }
        }
    }

    public PosterThumbJob? Complete(PosterThumbJob job)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(job.StoreDir, out var entry))
                return null;

            if (entry.FollowUpJob is not null)
            {
                var followUp = entry.FollowUpJob;
                entry.Job = followUp;
                entry.FollowUpJob = null;
                entry.Pending = false;
                entry.InFlight = true;
                return followUp;
            }

            _entries.TryRemove(job.StoreDir, out _);
            return null;
        }
    }

    private bool TryCoalesceLocked(PosterThumbJob job, out PosterThumbEnqueueResult result)
    {
        result = default;

        if (!_entries.TryGetValue(job.StoreDir, out var entry))
            return false;

        if (entry.Pending)
        {
            entry.Job = Merge(entry.Job, job);
            result = new PosterThumbEnqueueResult(PosterThumbEnqueueStatus.Coalesced);
            return true;
        }

        if (entry.InFlight)
        {
            entry.FollowUpJob = Merge(entry.FollowUpJob ?? entry.Job, job);
            result = new PosterThumbEnqueueResult(PosterThumbEnqueueStatus.Coalesced);
            return true;
        }

        return false;
    }

    private static PosterThumbJob Merge(PosterThumbJob existing, PosterThumbJob incoming)
    {
        var widths = MergeWidths(existing.Widths, incoming.Widths);
        var reason = existing.Reason == PosterThumbJobReason.MissingThumb || incoming.Reason == PosterThumbJobReason.MissingThumb
            ? PosterThumbJobReason.MissingThumb
            : existing.Reason == PosterThumbJobReason.Backfill || incoming.Reason == PosterThumbJobReason.Backfill
                ? PosterThumbJobReason.Backfill
                : PosterThumbJobReason.Warmup;
        var releaseId = incoming.ReleaseId ?? existing.ReleaseId;

        if (ReferenceEquals(widths, existing.Widths) && reason == existing.Reason && releaseId == existing.ReleaseId)
            return existing;

        return existing with
        {
            Widths = widths,
            Reason = reason,
            ReleaseId = releaseId
        };
    }

    private static IReadOnlyList<int>? MergeWidths(IReadOnlyList<int>? existing, IReadOnlyList<int>? incoming)
    {
        if (existing is null || incoming is null)
            return null;

        var set = new SortedSet<int>(existing);
        var changed = false;
        foreach (var width in incoming)
            changed |= set.Add(width);

        if (!changed && set.Count == existing.Count)
            return existing;

        return set.ToArray();
    }

    private sealed class QueueEntry
    {
        public QueueEntry(PosterThumbJob job)
        {
            Job = job;
        }

        public PosterThumbJob Job { get; set; }
        public bool Pending { get; set; }
        public bool InFlight { get; set; }
        public PosterThumbJob? FollowUpJob { get; set; }
    }
}
