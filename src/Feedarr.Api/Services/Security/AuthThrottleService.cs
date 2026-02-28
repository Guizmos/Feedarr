using System.Collections.Concurrent;
using Feedarr.Api.Options;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Services.Security;

public sealed class AuthThrottleService
{
    private sealed class AttemptState
    {
        public int Failures;
        public DateTimeOffset WindowStartedAtUtc;
        public DateTimeOffset BlockedUntilUtc;
        public DateTimeOffset LastSeenAtUtc;
    }

    private readonly ConcurrentDictionary<string, AttemptState> _states = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly BasicAuthThrottleOptions _options;
    private int _cleanupCounter;

    public AuthThrottleService(IOptions<BasicAuthThrottleOptions> options, TimeProvider timeProvider)
        : this(options.Value, timeProvider)
    {
    }

    internal AuthThrottleService(BasicAuthThrottleOptions options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
    }

    public bool TryGetRetryAfter(string remoteIp, string username, out TimeSpan retryAfter)
    {
        CleanupOccasionally();

        retryAfter = TimeSpan.Zero;
        var now = GetUtcNow();
        var key = BuildKey(remoteIp, username);
        if (!_states.TryGetValue(key, out var state))
            return false;

        lock (state)
        {
            ResetWindowIfExpired(state, now);
            if (state.BlockedUntilUtc <= now)
                return false;

            retryAfter = state.BlockedUntilUtc - now;
            return retryAfter > TimeSpan.Zero;
        }
    }

    public void RegisterFailure(string remoteIp, string username)
    {
        CleanupOccasionally();

        var now = GetUtcNow();
        var key = BuildKey(remoteIp, username);
        var state = _states.GetOrAdd(key, _ => new AttemptState
        {
            WindowStartedAtUtc = now
        });

        lock (state)
        {
            ResetWindowIfExpired(state, now);
            state.Failures++;
            state.LastSeenAtUtc = now;

            var backoff = GetBackoff(state.Failures);
            if (backoff > TimeSpan.Zero)
            {
                var blockedUntil = now.Add(backoff);
                if (blockedUntil > state.BlockedUntilUtc)
                    state.BlockedUntilUtc = blockedUntil;
            }
        }
    }

    public void RegisterSuccess(string remoteIp, string username)
    {
        CleanupOccasionally();
        _states.TryRemove(BuildKey(remoteIp, username), out _);
    }

    private DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();

    private void ResetWindowIfExpired(AttemptState state, DateTimeOffset now)
    {
        var window = TimeSpan.FromSeconds(Math.Max(1, _options.WindowSeconds));
        if (now - state.WindowStartedAtUtc < window)
            return;

        state.Failures = 0;
        state.WindowStartedAtUtc = now;
        state.BlockedUntilUtc = DateTimeOffset.MinValue;
    }

    private TimeSpan GetBackoff(int failures)
    {
        if (failures >= Math.Max(_options.HardBlockThreshold, _options.MediumBlockThreshold))
            return TimeSpan.FromSeconds(Math.Max(1, _options.HardBlockSeconds));
        if (failures >= Math.Max(_options.MediumBlockThreshold, _options.SoftBlockThreshold))
            return TimeSpan.FromSeconds(Math.Max(1, _options.MediumBlockSeconds));
        if (failures >= Math.Max(1, _options.SoftBlockThreshold))
            return TimeSpan.FromSeconds(Math.Max(1, _options.SoftBlockSeconds));

        return TimeSpan.Zero;
    }

    private static string BuildKey(string remoteIp, string username)
    {
        var normalizedIp = string.IsNullOrWhiteSpace(remoteIp) ? "unknown" : remoteIp.Trim();
        var normalizedUser = NormalizeUsername(username);
        return normalizedIp + "|" + normalizedUser;
    }

    private static string NormalizeUsername(string username)
    {
        var normalized = (username ?? string.Empty).Trim();
        if (normalized.Length > 128)
            normalized = normalized[..128];

        return normalized.ToLowerInvariant();
    }

    private void CleanupOccasionally()
    {
        if (Interlocked.Increment(ref _cleanupCounter) % 64 != 0)
            return;

        var now = GetUtcNow();
        var retention = TimeSpan.FromSeconds(Math.Max(_options.WindowSeconds, _options.HardBlockSeconds) * 2);
        foreach (var entry in _states)
        {
            var state = entry.Value;
            lock (state)
            {
                if (state.BlockedUntilUtc > now)
                    continue;
                if (now - state.LastSeenAtUtc < retention)
                    continue;
            }

            _states.TryRemove(entry.Key, out _);
        }
    }
}
