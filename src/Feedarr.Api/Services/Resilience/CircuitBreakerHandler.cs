using Feedarr.Api.Options;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Services.Resilience;

/// <summary>
/// Outermost DelegatingHandler that implements a consecutive-failures circuit breaker.
///
/// Rules:
///   • 5xx response          → failure (server error)
///   • HttpRequestException  → failure (network error, after inner retries)
///   • OperationCanceled     → failure only when not user-initiated (internal timeout)
///   • 4xx response          → neutral (client error — neither success nor failure)
///   • 2xx / 3xx response    → success, resets counter
///
/// The handler must be registered outermost (first AddHttpMessageHandler call) so it
/// sees the final result after all inner retries.
/// </summary>
internal abstract class CircuitBreakerHandler : DelegatingHandler
{
    private readonly CircuitBreakerState _state;
    private readonly int    _threshold;
    private readonly int    _breakDurationSeconds;
    private readonly string _family;
    private readonly ILogger _logger;

    protected CircuitBreakerHandler(
        CircuitBreakerState state,
        int    threshold,
        int    breakDurationSeconds,
        string family,
        ILogger logger)
    {
        _state               = state;
        _threshold           = threshold;
        _breakDurationSeconds = breakDurationSeconds;
        _family              = family;
        _logger              = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_state.IsOpen)
        {
            _logger.LogWarning("CB {Family}: circuit OPEN — fast-fail", _family);
            throw new HttpRequestException($"Circuit breaker is open for {_family}");
        }

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            var status   = (int)response.StatusCode;

            if (status >= 500)
                TryOpen(_state.RecordFailure(_threshold, _breakDurationSeconds));
            else if (status < 400)   // 2xx/3xx → success; 4xx → neutral (no state change)
                _state.RecordSuccess();

            return response;
        }
        catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested)
        {
            TryOpen(_state.RecordFailure(_threshold, _breakDurationSeconds));
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Internal timeout (not user-requested cancellation)
            TryOpen(_state.RecordFailure(_threshold, _breakDurationSeconds));
            throw;
        }

        void TryOpen(bool opened)
        {
            if (opened)
                _logger.LogWarning(
                    "CB {Family}: circuit OPENED after {Threshold} consecutive failures",
                    _family, _threshold);
        }
    }
}

/// <summary>Circuit breaker for Arr clients (Sonarr, Radarr, Eerr, Jackett, Prowlarr).</summary>
internal sealed class ArrCircuitBreakerHandler(
    ArrCircuitBreakerState state,
    IOptions<HttpResilienceOptions> opts,
    ILogger<ArrCircuitBreakerHandler> logger)
    : CircuitBreakerHandler(
        state,
        opts.Value.Arr.MinimumThroughput,
        opts.Value.Arr.BreakDurationSeconds,
        "Arr",
        logger);

/// <summary>Circuit breaker for external provider API clients.</summary>
internal sealed class ProviderCircuitBreakerHandler(
    ProviderCircuitBreakerState state,
    IOptions<HttpResilienceOptions> opts,
    ILogger<ProviderCircuitBreakerHandler> logger)
    : CircuitBreakerHandler(
        state,
        opts.Value.Providers.MinimumThroughput,
        opts.Value.Providers.BreakDurationSeconds,
        "Providers",
        logger);

/// <summary>Circuit breaker for Torznab indexer clients.</summary>
internal sealed class IndexerCircuitBreakerHandler(
    IndexerCircuitBreakerState state,
    IOptions<HttpResilienceOptions> opts,
    ILogger<IndexerCircuitBreakerHandler> logger)
    : CircuitBreakerHandler(
        state,
        opts.Value.Indexers.MinimumThroughput,
        opts.Value.Indexers.BreakDurationSeconds,
        "Indexers",
        logger);
