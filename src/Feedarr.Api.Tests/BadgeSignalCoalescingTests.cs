using System.Diagnostics;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Microsoft.Extensions.Logging;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class BadgeSignalCoalescingTests
{
    [Fact]
    public async Task NotifyBurst_CoalescesToSingleFlushWithinWindow()
    {
        var logger = new CapturingBadgeLogger();
        var signal = new BadgeSignal(
            OptionsFactory.Create(new AppOptions { BadgesSseCoalesceMs = 300 }),
            logger);

        for (var i = 0; i < 50; i++)
            signal.Notify("activity");

        await WaitUntilAsync(() => logger.FlushCount >= 1, TimeSpan.FromSeconds(2));
        await Task.Delay(380);

        Assert.Equal(1, logger.FlushCount);
        Assert.True(logger.MaxCoalescedCount >= 50);
    }

    [Fact]
    public async Task NotifySeparatedByWindow_TriggersTwoFlushes()
    {
        var logger = new CapturingBadgeLogger();
        var signal = new BadgeSignal(
            OptionsFactory.Create(new AppOptions { BadgesSseCoalesceMs = 250 }),
            logger);

        signal.Notify("activity");
        await WaitUntilAsync(() => logger.FlushCount >= 1, TimeSpan.FromSeconds(2));

        await Task.Delay(320);
        signal.Notify("activity");
        await WaitUntilAsync(() => logger.FlushCount >= 2, TimeSpan.FromSeconds(2));

        Assert.Equal(2, logger.FlushCount);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        var timeoutTicks = timeout.TotalSeconds * Stopwatch.Frequency;
        while (!predicate())
        {
            if (Stopwatch.GetTimestamp() - started > timeoutTicks)
                throw new TimeoutException("Condition not reached before timeout.");
            await Task.Delay(10);
        }
    }

    private sealed class CapturingBadgeLogger : ILogger<BadgeSignal>
    {
        private int _flushCount;
        private int _maxCoalescedCount;

        public int FlushCount => Volatile.Read(ref _flushCount);
        public int MaxCoalescedCount => Volatile.Read(ref _maxCoalescedCount);

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (!message.Contains("BadgeSignal flush", StringComparison.Ordinal))
                return;

            Interlocked.Increment(ref _flushCount);

            if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
            {
                foreach (var kv in kvps)
                {
                    if (!string.Equals(kv.Key, "CoalescedCount", StringComparison.Ordinal))
                        continue;

                    var parsed = kv.Value is null ? 0 : Convert.ToInt32(kv.Value);
                    UpdateMaxCoalescedCount(parsed);
                    break;
                }
            }
        }

        private void UpdateMaxCoalescedCount(int parsed)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxCoalescedCount);
                if (parsed <= current)
                    return;

                if (Interlocked.CompareExchange(ref _maxCoalescedCount, parsed, current) == current)
                    return;
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
