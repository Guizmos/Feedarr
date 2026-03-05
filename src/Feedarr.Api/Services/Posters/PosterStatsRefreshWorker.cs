using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace Feedarr.Api.Services.Posters;

public sealed class PosterStatsRefreshWorker : BackgroundService
{
    private const long ShortCooldownSeconds = 6 * 60 * 60;
    private const long LongCooldownSeconds = 24 * 60 * 60;

    private readonly ReleaseRepository _releases;
    private readonly ILogger<PosterStatsRefreshWorker> _log;
    private readonly TimeSpan _refreshPeriod;
    private readonly TimeProvider _timeProvider;
    private ReleaseRepository.PosterStatsWatermark? _lastWatermark;

    internal enum PosterStatsRefreshCycleResult
    {
        Refreshed = 0,
        SkippedUnchanged = 1,
    }

    public PosterStatsRefreshWorker(
        ReleaseRepository releases,
        IOptions<AppOptions> appOptions,
        ILogger<PosterStatsRefreshWorker> log,
        TimeProvider? timeProvider = null)
    {
        _releases = releases;
        _log = log;
        _timeProvider = timeProvider ?? TimeProvider.System;

        var refreshSeconds = Math.Clamp(appOptions.Value.PosterStatsRefreshSeconds, 5, 3600);
        _refreshPeriod = TimeSpan.FromSeconds(refreshSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RunRefreshCycle(stoppingToken);

        using var timer = new PeriodicTimer(_refreshPeriod);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    break;

                RunRefreshCycle(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Poster stats refresh failed; next retry in {DelaySeconds}s", _refreshPeriod.TotalSeconds);
            }
        }
    }

    internal PosterStatsRefreshCycleResult RunRefreshCycle(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();

        var watermark = _releases.GetPosterStatsWatermark();
        if (_lastWatermark.HasValue && _lastWatermark.Value == watermark)
        {
            sw.Stop();
            _log.LogInformation(
                "PosterStatsRefresh: skipped (unchanged) elapsedMs={ElapsedMs} watermark={Watermark}",
                sw.ElapsedMilliseconds,
                watermark);
            return PosterStatsRefreshCycleResult.SkippedUnchanged;
        }

        var nowTs = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        _releases.RefreshPosterStatsSnapshot(nowTs, ShortCooldownSeconds, LongCooldownSeconds);
        _lastWatermark = watermark;
        sw.Stop();

        _log.LogInformation(
            "PosterStatsRefresh: refreshed elapsedMs={ElapsedMs} watermark={Watermark}",
            sw.ElapsedMilliseconds,
            watermark);

        return PosterStatsRefreshCycleResult.Refreshed;
    }
}
