using System.Threading.Channels;

namespace Feedarr.Api.Services.Posters;

public sealed class PosterFetchQueue : IPosterFetchQueue
{
    private readonly Channel<PosterFetchJob> _channel;
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
        var result = _channel.Writer.TryWrite(job);
        if (result)
            Interlocked.Increment(ref _count);
        return result;
    }

    public async ValueTask<PosterFetchJob> DequeueAsync(CancellationToken ct)
    {
        var job = await _channel.Reader.ReadAsync(ct);
        Interlocked.Decrement(ref _count);
        return job;
    }

    public int ClearPending()
    {
        var cleared = 0;
        while (_channel.Reader.TryRead(out _))
        {
            cleared++;
            Interlocked.Decrement(ref _count);
        }
        return cleared;
    }
}
