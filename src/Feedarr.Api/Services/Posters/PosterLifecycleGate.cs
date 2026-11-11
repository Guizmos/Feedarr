using System.Collections.Concurrent;

namespace Feedarr.Api.Services.Posters;

// Publishers are concurrent. A bounded collector never waits for active publishers;
// new publishers only wait for the current short GC transaction/file deletion.
internal sealed class PosterLifecycleGate
{
    private static readonly ConcurrentDictionary<string, PosterLifecycleGate> Gates = new(StringComparer.Ordinal);
    internal static PosterLifecycleGate ForDatabase(string path) => Gates.GetOrAdd(Path.GetFullPath(path), _ => new());
    private readonly object _sync = new();
    private int _publishers;
    private TaskCompletionSource? _gc;

    internal async ValueTask<IDisposable> EnterPublicationAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task wait;
            lock (_sync)
            {
                if (_gc is null) { _publishers++; return new Lease(this, false); }
                wait = _gc.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    internal bool HasPublications { get { lock (_sync) return _publishers > 0; } }

    internal IDisposable EnterPublication() => EnterPublicationAsync(default).AsTask().GetAwaiter().GetResult();

    internal IDisposable? TryEnterCollection()
    {
        lock (_sync)
        {
            if (_gc is not null || _publishers != 0) return null;
            _gc = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new Lease(this, true);
        }
    }

    private sealed class Lease(PosterLifecycleGate owner, bool collection) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._sync)
            {
                if (!collection) { owner._publishers--; return; }
                var completed = owner._gc;
                owner._gc = null;
                completed!.TrySetResult();
            }
        }
    }
}
