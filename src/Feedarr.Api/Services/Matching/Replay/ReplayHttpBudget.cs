namespace Feedarr.Api.Services.Matching.Replay;

internal sealed class ReplayBudgetExceededException : Exception;
internal sealed class ReplayHttpBudget(int maximum, TimeSpan? spacing = null)
{
    internal static readonly HttpRequestOptionsKey<ReplayHttpBudget> Option = new("feedarr.matching.replay.budget");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _spacing = spacing ?? TimeSpan.FromMilliseconds(250);
    private long _lastSent;
    private int _used;
    internal int Maximum { get; } = maximum is >= 1 and <= 30 ? maximum : throw new ArgumentOutOfRangeException(nameof(maximum));
    internal int Used => Volatile.Read(ref _used);
    internal async Task BeforeSendAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_used >= Maximum) throw new ReplayBudgetExceededException();
            if (_lastSent != 0)
            {
                var remaining = _spacing - System.Diagnostics.Stopwatch.GetElapsedTime(_lastSent);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _used); // Failed attempts count too. Innermost handler counts redirects individually.
            _lastSent = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        finally { _gate.Release(); }
    }
}

internal sealed class ReplayBudgetHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri;
        if (!request.Options.TryGetValue(ReplayHttpBudget.Option, out var budget) || uri is null ||
            uri.Scheme != "https" || uri.Host != "api.themoviedb.org" || uri.Port != 443 ||
            (uri.AbsolutePath != "/3/search/movie" && uri.AbsolutePath != "/3/search/tv"))
            throw new InvalidOperationException("replay_destination_denied");
        await budget.BeforeSendAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }
}
