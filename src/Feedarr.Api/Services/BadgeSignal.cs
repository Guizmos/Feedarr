using System.Collections.Concurrent;
using System.Threading.Channels;
using Feedarr.Api.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Services;

public sealed class BadgeSignal
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subs = new();
    private readonly object _flushLock = new();
    private readonly TimeSpan _coalesceWindow;
    private readonly ILogger<BadgeSignal> _log;
    private Task? _flushLoopTask;
    private bool _dirty;
    private int _pendingNotifyCount;
    private string _lastType = "activity";

    public BadgeSignal()
        : this(OptionsFactory.Create(new AppOptions()), NullLogger<BadgeSignal>.Instance)
    {
    }

    public BadgeSignal(IOptions<AppOptions> options, ILogger<BadgeSignal> log)
    {
        var configuredMs = options?.Value?.BadgesSseCoalesceMs ?? 750;
        var boundedMs = Math.Clamp(configuredMs, 100, 5000);
        _coalesceWindow = TimeSpan.FromMilliseconds(boundedMs);
        _log = log ?? NullLogger<BadgeSignal>.Instance;
    }

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
        lock (_flushLock)
        {
            _dirty = true;
            _pendingNotifyCount++;
            _lastType = string.IsNullOrWhiteSpace(type) ? "activity" : type.Trim();

            if (_flushLoopTask is null || _flushLoopTask.IsCompleted)
                _flushLoopTask = RunFlushLoopAsync();
        }
    }

    private async Task RunFlushLoopAsync()
    {
        while (true)
        {
            await Task.Delay(_coalesceWindow).ConfigureAwait(false);

            int coalescedCount;
            string eventType;

            lock (_flushLock)
            {
                if (!_dirty)
                {
                    _flushLoopTask = null;
                    return;
                }

                _dirty = false;
                coalescedCount = _pendingNotifyCount;
                _pendingNotifyCount = 0;
                eventType = _lastType;
            }

            Broadcast(eventType);

            _log.LogDebug(
                "BadgeSignal flush event={EventType} coalesced={CoalescedCount} subscribers={Subscribers}",
                eventType,
                coalescedCount,
                _subs.Count);

            lock (_flushLock)
            {
                if (_dirty)
                    continue;

                _flushLoopTask = null;
                return;
            }
        }
    }

    private void Broadcast(string eventType)
    {
        foreach (var ch in _subs.Values)
        {
            ch.Writer.TryWrite(eventType);
        }
    }
}
