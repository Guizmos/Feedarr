using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Feedarr.Api.Services;

public sealed class BadgeSignal
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subs = new();

    public IAsyncEnumerable<string> Subscribe(CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _subs[id] = channel;

        ct.Register(() =>
        {
            if (_subs.TryRemove(id, out var removed))
            {
                removed.Writer.TryComplete();
            }
        });

        return channel.Reader.ReadAllAsync(ct);
    }

    public void Notify(string type)
    {
        foreach (var ch in _subs.Values)
        {
            ch.Writer.TryWrite(type);
        }
    }
}
