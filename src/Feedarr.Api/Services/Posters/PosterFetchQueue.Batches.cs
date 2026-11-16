namespace Feedarr.Api.Services.Posters;

public sealed record PosterBatchProgress(int Total, int Done, int Pending, int Succeeded, int Failed, int Cancelled, int NotAccepted, bool Terminal);

public sealed partial class PosterFetchQueue
{
    // Short-lived observers of existing queue entries, not a second job queue.
    private sealed class TrackedItem { internal QueueEntry? Entry; internal string? Outcome; }
    private sealed record Batch(DateTimeOffset Created, Dictionary<long, TrackedItem> Items);
    private readonly Dictionary<Guid, Batch> _batches = new();

    public Guid? BeginBatch(IReadOnlyList<long> ids)
    {
        lock (_gate)
        {
            foreach (var key in _batches.Where(p => DateTimeOffset.UtcNow - p.Value.Created > TimeSpan.FromHours(1)).Select(p => p.Key).ToArray()) _batches.Remove(key);
            foreach (var key in _batches.Where(p => p.Value.Items.Values.All(i => i.Outcome is not null)).OrderBy(p => p.Value.Created).Take(Math.Max(0, _batches.Count - 63)).Select(p => p.Key).ToArray()) _batches.Remove(key);
            if (_batches.Count >= 64) throw new InvalidOperationException("too_many_retro_batches");
            var id = Guid.NewGuid();
            _batches[id] = new(DateTimeOffset.UtcNow, ids.Distinct().ToDictionary(i => i,
                i => new TrackedItem()));
            return id;
        }
    }
    private void AttachTrackedEntry(long id, QueueEntry entry)
    {
        foreach (var batch in _batches.Values)
            if (batch.Items.TryGetValue(id, out var item) && item.Entry is null && item.Outcome is null) item.Entry = entry;
    }
    private void FinishTrackedEntry(QueueEntry entry, string outcome)
    {
        foreach (var batch in _batches.Values)
            foreach (var item in batch.Items.Values)
                if (ReferenceEquals(item.Entry, entry) && item.Outcome is null) { item.Outcome = outcome; item.Entry = null; }
    }
    public void SealBatch(Guid? id)
    {
        lock (_gate)
            if (id.HasValue && _batches.TryGetValue(id.Value, out var batch))
                foreach (var item in batch.Items.Values)
                    if (item.Entry is null && item.Outcome is null) item.Outcome = "not_accepted";
    }
    public PosterBatchProgress? GetBatchProgress(Guid id)
    {
        lock (_gate)
        {
            if (!_batches.TryGetValue(id, out var batch)) return null;
            var items = batch.Items.Values.ToArray();
            var done = items.Count(i => i.Outcome is not null);
            return new(items.Length, done, items.Length - done, items.Count(i => i.Outcome == "succeeded"),
                items.Count(i => i.Outcome == "failed"), items.Count(i => i.Outcome == "cancelled"),
                items.Count(i => i.Outcome == "not_accepted"), done == items.Length);
        }
    }
    public int CountOutstanding(IReadOnlyList<long> ids)
    {
        lock (_gate) return ids.Distinct().Count(id => _entries.ContainsKey(id));
    }
}
