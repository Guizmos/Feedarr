using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Feedarr.Api.Services.Posters;

public sealed partial class PosterFetchQueue : IPosterFetchQueue
{
    private const int Capacity = 2000;
    public static readonly TimeSpan DefaultEnqueueTimeout = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultBatchEnqueueTimeout = TimeSpan.FromSeconds(2);

    // The channel carries wake-up/capacity tokens, never IDs. Removing/promoting
    // a FIFO node cannot leave a duplicate or stale token behind.
    private readonly Channel<byte> _channel;
    private readonly LinkedList<long> _normal = new();
    private readonly LinkedList<long> _background = new();
    private const int NormalBurstLimit = 4;
    private int _normalBurst;
    private readonly ConcurrentDictionary<long, QueueEntry> _entries = new();
    private readonly ILogger<PosterFetchQueue> _log;
    private readonly object _gate = new();

    private long _jobsEnqueued;
    private long _jobsCoalesced;
    private long _jobsTimedOut;
    private long _jobsProcessed;
    private long _jobsSucceeded;
    private long _jobsFailed;
    private long _jobsRetried;
    private long _inFlightCount;
    private long? _lastJobStartedAtTs;
    private long? _lastJobEndedAtTs;
    private PosterFetchCurrentJobSnapshot? _currentJob;

    public PosterFetchQueue(ILogger<PosterFetchQueue> log)
    {
        _log = log;
        _channel = Channel.CreateBounded<byte>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public int Count { get { lock (_gate) return _normal.Count + _background.Count; } }

    public async ValueTask<PosterFetchEnqueueResult> EnqueueAsync(PosterFetchJob job, CancellationToken ct, TimeSpan timeout)
    {
        if (job.ItemId <= 0)
            return new PosterFetchEnqueueResult(PosterFetchEnqueueStatus.Rejected);

        var hasTimeout = timeout > TimeSpan.Zero;
        var deadlineUtc = hasTimeout ? DateTimeOffset.UtcNow + timeout : DateTimeOffset.UtcNow;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            lock (_gate)
            {
                var status = TryEnqueueLocked(job);
                if (status != PosterFetchEnqueueStatus.TimedOut)
                    return new PosterFetchEnqueueResult(status);
            }

            if (!hasTimeout || DateTimeOffset.UtcNow >= deadlineUtc)
            {
                Interlocked.Increment(ref _jobsTimedOut);
                return new PosterFetchEnqueueResult(PosterFetchEnqueueStatus.TimedOut);
            }

            var remaining = deadlineUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                Interlocked.Increment(ref _jobsTimedOut);
                return new PosterFetchEnqueueResult(PosterFetchEnqueueStatus.TimedOut);
            }

            using var timeoutCts = new CancellationTokenSource(remaining);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                var canWrite = await _channel.Writer.WaitToWriteAsync(linkedCts.Token).ConfigureAwait(false);
                if (!canWrite)
                {
                    Interlocked.Increment(ref _jobsTimedOut);
                    return new PosterFetchEnqueueResult(PosterFetchEnqueueStatus.TimedOut);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref _jobsTimedOut);
                return new PosterFetchEnqueueResult(PosterFetchEnqueueStatus.TimedOut);
            }
        }
    }

    public async ValueTask<PosterFetchEnqueueBatchResult> EnqueueManyAsync(IReadOnlyList<PosterFetchJob> jobs, CancellationToken ct, TimeSpan timeout)
    {
        if (jobs is null || jobs.Count == 0)
            return default;

        var queue = new Queue<PosterFetchJob>(jobs.Count);
        var rejected = 0;
        foreach (var job in jobs)
        {
            if (job.ItemId <= 0)
                rejected++;
            else
                queue.Enqueue(job);
        }

        var enqueued = 0;
        var coalesced = 0;
        var timedOut = 0;
        var hasTimeout = timeout > TimeSpan.Zero;
        var deadlineUtc = hasTimeout ? DateTimeOffset.UtcNow + timeout : DateTimeOffset.UtcNow;

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var pending = new Queue<PosterFetchJob>(queue.Count);
            lock (_gate)
            {
                while (queue.Count > 0)
                {
                    var job = queue.Dequeue();
                    var status = TryEnqueueLocked(job);
                    switch (status)
                    {
                        case PosterFetchEnqueueStatus.Enqueued:
                            enqueued++;
                            break;
                        case PosterFetchEnqueueStatus.Coalesced:
                            coalesced++;
                            break;
                        case PosterFetchEnqueueStatus.Rejected:
                            rejected++;
                            break;
                        default:
                            pending.Enqueue(job);
                            break;
                    }
                }
            }

            if (pending.Count == 0)
                break;

            if (!hasTimeout || DateTimeOffset.UtcNow >= deadlineUtc)
            {
                timedOut += pending.Count;
                Interlocked.Add(ref _jobsTimedOut, pending.Count);
                break;
            }

            var remaining = deadlineUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                timedOut += pending.Count;
                Interlocked.Add(ref _jobsTimedOut, pending.Count);
                break;
            }

            using var timeoutCts = new CancellationTokenSource(remaining);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                var canWrite = await _channel.Writer.WaitToWriteAsync(linkedCts.Token).ConfigureAwait(false);
                if (!canWrite)
                {
                    timedOut += pending.Count;
                    Interlocked.Add(ref _jobsTimedOut, pending.Count);
                    break;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut += pending.Count;
                Interlocked.Add(ref _jobsTimedOut, pending.Count);
                break;
            }

            queue = pending;
        }

        return new PosterFetchEnqueueBatchResult(
            Enqueued: enqueued,
            Coalesced: coalesced,
            TimedOut: timedOut,
            Rejected: rejected);
    }

    public async ValueTask<PosterFetchJob> DequeueAsync(CancellationToken ct)
    {
        while (true)
        {
            await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                // Consume the token and FIFO node under the same lock as producers:
                // total pending jobs stays bounded even with two waiting workers.
                if (!_channel.Reader.TryRead(out _)) continue;
                var background = _background.Count > 0 && (_normal.Count == 0 || _normalBurst >= NormalBurstLimit);
                var fifo = background ? _background : _normal;
                var node = fifo.First!;
                fifo.RemoveFirst();
                var entry = _entries[node.Value];
                entry.PendingNode = null;
                _normalBurst = background ? 0 : Math.Min(NormalBurstLimit, _normalBurst + 1);
                entry.Pending = false;
                entry.InFlight = true;

                var startedAtTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _lastJobStartedAtTs = startedAtTs;
                _currentJob = new PosterFetchCurrentJobSnapshot(entry.Job.ItemId, entry.Job.ForceRefresh, startedAtTs);
                Interlocked.Increment(ref _inFlightCount);
                return entry.Job;
            }
        }
    }

    public void RecordRetry()
        => Interlocked.Increment(ref _jobsRetried);

    public PosterFetchJob? Complete(PosterFetchJob job, PosterFetchProcessResult result)
    {
        lock (_gate)
        {
            var endedAtTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _lastJobEndedAtTs = endedAtTs;
            Interlocked.Increment(ref _jobsProcessed);
            if (result.Succeeded)
                Interlocked.Increment(ref _jobsSucceeded);
            else
                Interlocked.Increment(ref _jobsFailed);

            if (!_entries.TryGetValue(job.ItemId, out var entry))
            {
                _currentJob = null;
                DecrementInFlightCount();
                return null;
            }

            if (entry.FollowUpJob is not null)
            {
                var followUp = entry.FollowUpJob with { AttemptCount = 0 };
                entry.FollowUpJob = null;
                entry.Job = followUp;
                entry.InFlight = true;

                var startedAtTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _lastJobStartedAtTs = startedAtTs;
                _currentJob = new PosterFetchCurrentJobSnapshot(followUp.ItemId, followUp.ForceRefresh, startedAtTs);
                return followUp;
            }

            FinishTrackedEntry(entry, result.Succeeded ? "succeeded" : "failed");
            _entries.TryRemove(job.ItemId, out _);
            _currentJob = null;
            DecrementInFlightCount();
            return null;
        }
    }

    public int ClearPending()
    {
        int cleared;
        lock (_gate)
        {
            cleared = _normal.Count + _background.Count;
            foreach (var itemId in _normal.Concat(_background))
                if (_entries.TryRemove(itemId, out var removed)) FinishTrackedEntry(removed, "cancelled");
            _normal.Clear();
            _background.Clear();
            _normalBurst = 0;
            while (_channel.Reader.TryRead(out _)) { }
        }

        if (cleared > 0)
            _log.LogInformation("PosterFetchQueue cleared {Cleared} pending jobs", cleared);

        return cleared;
    }

    public PosterFetchQueueSnapshot GetSnapshot()
    {
        long? oldestQueuedAgeMs = null;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        PosterFetchCurrentJobSnapshot? currentJob;
        long? lastJobStartedAtTs;
        long? lastJobEndedAtTs;

        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                if (!entry.Pending)
                    continue;

                var age = Math.Max(0, nowMs - entry.EnqueuedAtMs);
                if (!oldestQueuedAgeMs.HasValue || age > oldestQueuedAgeMs.Value)
                    oldestQueuedAgeMs = age;
            }

            currentJob = _currentJob;
            lastJobStartedAtTs = _lastJobStartedAtTs;
            lastJobEndedAtTs = _lastJobEndedAtTs;
        }

        var pendingCount = Count;
        var inFlightCount = (int)Interlocked.Read(ref _inFlightCount);
        return new PosterFetchQueueSnapshot(
            PendingCount: pendingCount,
            InFlightCount: inFlightCount,
            IsProcessing: pendingCount > 0 || inFlightCount > 0,
            OldestQueuedAgeMs: oldestQueuedAgeMs,
            LastJobStartedAtTs: lastJobStartedAtTs,
            LastJobEndedAtTs: lastJobEndedAtTs,
            CurrentJob: currentJob,
            JobsEnqueued: Interlocked.Read(ref _jobsEnqueued),
            JobsCoalesced: Interlocked.Read(ref _jobsCoalesced),
            JobsTimedOut: Interlocked.Read(ref _jobsTimedOut),
            JobsProcessed: Interlocked.Read(ref _jobsProcessed),
            JobsSucceeded: Interlocked.Read(ref _jobsSucceeded),
            JobsFailed: Interlocked.Read(ref _jobsFailed),
            JobsRetried: Interlocked.Read(ref _jobsRetried));
    }

    private bool TryCoalesceLocked(PosterFetchJob job, out PosterFetchEnqueueResult result)
    {
        result = default;

        if (!_entries.TryGetValue(job.ItemId, out var entry))
            return false;

        if (entry.Pending)
        {
            var merged = MergePendingJob(entry.Job, job);
            if (entry.Job.Priority == PosterFetchPriority.Background && merged.Priority == PosterFetchPriority.Normal)
            {
                _background.Remove(entry.PendingNode!);
                entry.PendingNode = _normal.AddLast(job.ItemId);
            }
            entry.Job = merged;
            Interlocked.Increment(ref _jobsCoalesced);
            result = new PosterFetchEnqueueResult(PosterFetchEnqueueStatus.Coalesced);
            return true;
        }

        if (entry.InFlight)
        {
            if (job.ForceRefresh && !entry.Job.ForceRefresh)
                entry.FollowUpJob = MergeFollowUpJob(entry.FollowUpJob, entry.Job, job);

            Interlocked.Increment(ref _jobsCoalesced);
            result = new PosterFetchEnqueueResult(PosterFetchEnqueueStatus.Coalesced);
            return true;
        }

        return false;
    }

    private PosterFetchEnqueueStatus TryEnqueueLocked(PosterFetchJob job)
    {
        if (TryCoalesceLocked(job, out var coalesced))
        {
            AttachTrackedEntry(job.ItemId, _entries[job.ItemId]);
            return coalesced.Status;
        }

        if (_channel.Writer.TryWrite(0))
        {
            _entries[job.ItemId] = new QueueEntry(job, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                Pending = true,
                PendingNode = (job.Priority == PosterFetchPriority.Background ? _background : _normal).AddLast(job.ItemId)
            };
            AttachTrackedEntry(job.ItemId, _entries[job.ItemId]);
            Interlocked.Increment(ref _jobsEnqueued);
            return PosterFetchEnqueueStatus.Enqueued;
        }

        return PosterFetchEnqueueStatus.TimedOut;
    }

    private static PosterFetchJob MergePendingJob(PosterFetchJob existing, PosterFetchJob incoming)
    {
        var forceRefresh = existing.ForceRefresh || incoming.ForceRefresh;
        var retroLogFile = !string.IsNullOrWhiteSpace(incoming.RetroLogFile)
            ? incoming.RetroLogFile
            : existing.RetroLogFile;

        var priority = existing.Priority == PosterFetchPriority.Normal || incoming.Priority == PosterFetchPriority.Normal
            ? PosterFetchPriority.Normal : PosterFetchPriority.Background;
        if (forceRefresh == existing.ForceRefresh && retroLogFile == existing.RetroLogFile && priority == existing.Priority)
            return existing;

        return existing with
        {
            ForceRefresh = forceRefresh,
            RetroLogFile = retroLogFile,
            Priority = priority
        };
    }

    private static PosterFetchJob MergeFollowUpJob(PosterFetchJob? existingFollowUp, PosterFetchJob inFlight, PosterFetchJob incoming)
    {
        var baseJob = existingFollowUp ?? inFlight;
        return baseJob with
        {
            ForceRefresh = true,
            Priority = baseJob.Priority == PosterFetchPriority.Normal || incoming.Priority == PosterFetchPriority.Normal
                ? PosterFetchPriority.Normal : PosterFetchPriority.Background,
            AttemptCount = 0,
            RetroLogFile = !string.IsNullOrWhiteSpace(incoming.RetroLogFile)
                ? incoming.RetroLogFile
                : baseJob.RetroLogFile
        };
    }

    private void DecrementInFlightCount()
    {
        while (true)
        {
            var snapshot = Interlocked.Read(ref _inFlightCount);
            if (snapshot <= 0)
                return;

            if (Interlocked.CompareExchange(ref _inFlightCount, snapshot - 1, snapshot) == snapshot)
                return;
        }
    }

    private sealed class QueueEntry
    {
        public QueueEntry(PosterFetchJob job, long enqueuedAtMs)
        {
            Job = job;
            EnqueuedAtMs = enqueuedAtMs;
        }

        public PosterFetchJob Job { get; set; }
        public long EnqueuedAtMs { get; }
        public LinkedListNode<long>? PendingNode { get; set; }
        public bool Pending { get; set; }
        public bool InFlight { get; set; }
        public PosterFetchJob? FollowUpJob { get; set; }
    }
}
