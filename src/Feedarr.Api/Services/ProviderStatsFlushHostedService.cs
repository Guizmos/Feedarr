using Feedarr.Api.Options;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Services;

public sealed class ProviderStatsFlushHostedService : BackgroundService
{
    private const int FailureLogDebounceMs = 30_000;

    private readonly ProviderStatsService _stats;
    private readonly ProviderStatsFlushOptions _options;
    private readonly ILogger<ProviderStatsFlushHostedService> _logger;
    private long _lastFailureLogTicks;

    public ProviderStatsFlushHostedService(
        ProviderStatsService stats,
        IOptions<ProviderStatsFlushOptions> options,
        ILogger<ProviderStatsFlushHostedService> logger)
    {
        _stats = stats;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableFlush)
            return;

        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.FlushIntervalSeconds, 1, 300));
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushOnceAsync(stoppingToken).ConfigureAwait(false);            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (!_options.EnableFlush)
            return;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
        await FlushOnceAsync(timeoutCts.Token).ConfigureAwait(false);    }

    private async Task FlushOnceAsync(CancellationToken ct)
    {
        try
        {
            await _stats.FlushAsync(ct).ConfigureAwait(false);            Volatile.Write(ref _lastFailureLogTicks, 0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var nowTicks = Environment.TickCount64;
            var previous = Volatile.Read(ref _lastFailureLogTicks);
            if (previous == 0 || nowTicks - previous >= FailureLogDebounceMs)
            {
                Volatile.Write(ref _lastFailureLogTicks, nowTicks);
                _logger.LogWarning(ex, "Provider stats flush failed; pending deltas will be retried");
            }
        }
    }
}
