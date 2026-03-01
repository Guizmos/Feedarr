namespace Feedarr.Api.Services.Resilience;

/// <summary>
/// Thread-safe consecutive-failures circuit breaker state.
/// Closed → Open after <c>threshold</c> consecutive failures.
/// Open → automatically allows a probe after <c>breakDurationSeconds</c>.
/// </summary>
public class CircuitBreakerState
{
    private int  _consecutiveFailures;
    private long _openUntilTicks = long.MinValue;

    /// <summary>Returns true while the circuit is open (requests should fast-fail).</summary>
    public bool IsOpen =>
        Environment.TickCount64 < Volatile.Read(ref _openUntilTicks);

    /// <summary>
    /// Records a successful response. Resets the consecutive-failure counter
    /// and closes the circuit.
    /// </summary>
    public void RecordSuccess()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        Volatile.Write(ref _openUntilTicks, long.MinValue);
    }

    /// <summary>
    /// Records a failure. Opens the circuit if the failure count reaches the threshold.
    /// </summary>
    /// <returns>true if the circuit just transitioned to Open.</returns>
    public bool RecordFailure(int threshold, int breakDurationSeconds)
    {
        var failures = Interlocked.Increment(ref _consecutiveFailures);
        if (failures >= threshold)
        {
            var until = Environment.TickCount64 + (long)breakDurationSeconds * 1000;
            Volatile.Write(ref _openUntilTicks, until);
            return true;
        }
        return false;
    }

    /// <summary>Exposed for unit tests only — reads the current consecutive failure count.</summary>
    internal int ConsecutiveFailuresForTest =>
        Volatile.Read(ref _consecutiveFailures);
}

/// <summary>Singleton circuit breaker state for Arr clients (Sonarr/Radarr/Eerr/Jackett/Prowlarr).</summary>
public sealed class ArrCircuitBreakerState      : CircuitBreakerState { }

/// <summary>Singleton circuit breaker state for external provider API clients.</summary>
public sealed class ProviderCircuitBreakerState : CircuitBreakerState { }

/// <summary>Singleton circuit breaker state for Torznab indexer clients.</summary>
public sealed class IndexerCircuitBreakerState  : CircuitBreakerState { }
