using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;

namespace Feedarr.Api.Services.Matching;

// Responses only: never stores a ranker decision or a release identity.
internal sealed class ProviderResponseCache : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 256 });
    private readonly Dictionary<string, Flight> _flights = new();
    private readonly object _gate = new();
    private readonly TimeSpan _ttl;
    internal ProviderResponseCache(TimeSpan? ttl = null) => _ttl = ttl ?? TimeSpan.FromMinutes(2);

    private sealed class Flight
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource<object?> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Waiters;
    }

    internal async Task<T?> GetAsync<T>(string key, Func<CancellationToken, Task<T?>> fetch,
        Func<T, bool> complete, CancellationToken ct) where T : class
    {
        ct.ThrowIfCancellationRequested();
        Flight? flight;
        bool owner = false;
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out T? cached)) return cached;
            if (!_flights.TryGetValue(key, out flight))
            {
                // Bound in-flight bookkeeping too; excess distinct requests simply bypass caching.
                if (_flights.Count < 128)
                {
                    flight = new Flight();
                    _flights.Add(key, flight);
                    owner = true;
                }
            }
            if (flight is not null) flight.Waiters++;
        }
        if (flight is null) return await fetch(ct).ConfigureAwait(false);
        if (owner) _ = FillAsync(key, flight, fetch, complete);
        try { return (T?)await flight.Result.Task.WaitAsync(ct).ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                if (--flight.Waiters == 0 && !flight.Result.Task.IsCompleted)
                {
                    _flights.Remove(key);
                    flight.Cancellation.Cancel();
                }
            }
        }
    }

    private async Task FillAsync<T>(string key, Flight flight, Func<CancellationToken, Task<T?>> fetch,
        Func<T, bool> complete) where T : class
    {
        try
        {
            var value = await fetch(flight.Cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!flight.Cancellation.IsCancellationRequested && value is not null && complete(value) &&
                    JsonSerializer.Serialize(value).Length <= 65536)
                    _cache.Set(key, value, new MemoryCacheEntryOptions().SetSize(1).SetAbsoluteExpiration(_ttl));
                flight.Result.TrySetResult(value);
            }
        }
        catch (OperationCanceledException) { flight.Result.TrySetCanceled(); }
        catch (Exception e) { flight.Result.TrySetException(e); }
        finally
        {
            lock (_gate)
                if (_flights.TryGetValue(key, out var current) && ReferenceEquals(current, flight)) _flights.Remove(key);
            flight.Cancellation.Dispose();
            // Observe errors even when every caller has cancelled its wait.
            _ = flight.Result.Task.Exception;
        }
    }

    public void Dispose() => _cache.Dispose();
}
