namespace Feedarr.Api.Services.Matching.Replay;

internal class ReplayBudgetExceededException : Exception;
internal sealed class ReplayCategoryBudgetExceededException : ReplayBudgetExceededException;
internal sealed class ReplayHttpBudget(int maximum, TimeSpan? spacing = null, ReplayHttpBudget? parent = null)
{
    internal static readonly HttpRequestOptionsKey<ReplayHttpBudget> Option = new("feedarr.matching.replay.budget");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _spacing = spacing ?? TimeSpan.FromMilliseconds(250);
    private long _lastSent;
    private int _used;
    internal int Maximum { get; } = maximum >= (parent is null ? 1 : 0) && maximum <= (parent?.Maximum ?? 30) ? maximum : throw new ArgumentOutOfRangeException(nameof(maximum));
    internal int Used => Volatile.Read(ref _used);
    internal async Task BeforeSendAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_used >= Maximum)
            {
                if (parent is not null) throw new ReplayCategoryBudgetExceededException();
                throw new ReplayBudgetExceededException();
            }
            if (parent is not null)
            {
                await parent.BeforeSendAsync(ct).ConfigureAwait(false);
                Interlocked.Increment(ref _used);
                return;
            }
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
    private static bool Allowed(Uri uri) =>
        uri.Host == "api.themoviedb.org" && (uri.AbsolutePath is "/3/search/movie" or "/3/search/tv" ||
            System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, @"^/3/tv/[1-9][0-9]*(/season/[0-9]+/episode/[0-9]+)?$")) ||
        uri.Host == "api.jikan.moe" && (uri.AbsolutePath == "/v4/anime" ||
            System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, @"^/v4/anime/[1-9][0-9]*(/full|/episodes/[1-9][0-9]*)$")) ||
        uri.Host == "api.tvmaze.com" && (uri.AbsolutePath == "/search/shows" ||
            System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, @"^/shows/[1-9][0-9]*(/episodes|/episodesbydate)?$"));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri;
        if (!request.Options.TryGetValue(ReplayHttpBudget.Option, out var budget) || uri is null ||
            uri.Scheme != "https" || uri.Port != 443 || request.Method != HttpMethod.Get ||
            !Allowed(uri))
            throw new InvalidOperationException("replay_destination_denied");
        await budget.BeforeSendAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }
}
