using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Services.Posters;

public sealed class MissingPosterSweepWorker : BackgroundService
{
    private const long DefaultShortCooldownSeconds = 15 * 60;
    private const long DefaultHardFailCooldownSeconds = 24 * 60 * 60;

    private readonly ReleaseRepository _releases;
    private readonly PosterFetchJobFactory _jobs;
    private readonly IPosterFetchQueue _queue;
    private readonly ILogger<MissingPosterSweepWorker> _log;
    private readonly TimeSpan _sweepPeriod;
    private readonly int _batchSize;
    private readonly long _shortCooldownSeconds;
    private readonly long _hardFailCooldownSeconds;

    internal readonly record struct MissingPosterSweepResult(
        int Found,
        int Requested,
        int Enqueued,
        int Coalesced,
        int TimedOut,
        int Rejected);

    public MissingPosterSweepWorker(
        ReleaseRepository releases,
        PosterFetchJobFactory jobs,
        IPosterFetchQueue queue,
        IOptions<AppOptions> options,
        ILogger<MissingPosterSweepWorker> log)
    {
        _releases = releases;
        _jobs = jobs;
        _queue = queue;
        _log = log;

        var opt = options.Value;
        _sweepPeriod = TimeSpan.FromMinutes(Math.Clamp(opt.MissingPosterSweepMinutes, 5, 60));
        _batchSize = Math.Clamp(opt.MissingPosterSweepBatchSize, 1, 1000);
        _shortCooldownSeconds = Math.Clamp(
            (long)(opt.MissingPosterSweepShortCooldownMinutes <= 0
                ? DefaultShortCooldownSeconds / 60
                : opt.MissingPosterSweepShortCooldownMinutes) * 60L,
            30L,
            7L * 24L * 60L * 60L);
        _hardFailCooldownSeconds = Math.Clamp(
            (long)(opt.MissingPosterSweepHardFailCooldownMinutes <= 0
                ? DefaultHardFailCooldownSeconds / 60
                : opt.MissingPosterSweepHardFailCooldownMinutes) * 60L,
            _shortCooldownSeconds,
            14L * 24L * 60L * 60L);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepOnceSafeAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_sweepPeriod);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    break;

                await SweepOnceSafeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Missing poster sweep failed; next retry in {DelayMinutes} minutes",
                    _sweepPeriod.TotalMinutes);
            }
        }
    }

    internal async Task<MissingPosterSweepResult> SweepOnceAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var counts = await _releases.GetMissingPosterActionableCountsAsync(
            nowTs,
            _shortCooldownSeconds,
            _hardFailCooldownSeconds,
            ct).ConfigureAwait(false);

        var ids = await _releases.GetReleaseIdsMissingPosterActionableAsync(
            _batchSize,
            nowTs,
            _shortCooldownSeconds,
            _hardFailCooldownSeconds,
            ct).ConfigureAwait(false);
        var filteredByCooldown = Math.Max(0, counts.TotalMissing - counts.ActionableMissing);

        if (ids.Count == 0)
        {
            _log.LogDebug(
                "Missing poster sweep: no actionable posters found totalCandidates={TotalCandidates} filteredByCooldown={FilteredByCooldown} batchSize={BatchSize} shortCooldownSeconds={ShortCooldownSeconds} hardFailCooldownSeconds={HardFailCooldownSeconds}",
                counts.TotalMissing,
                filteredByCooldown,
                _batchSize,
                _shortCooldownSeconds,
                _hardFailCooldownSeconds);
            return default;
        }

        var jobs = new List<PosterFetchJob>(ids.Count);
        foreach (var id in ids)
        {
            var job = _jobs.Create(id, forceRefresh: false);
            if (job is not null)
                jobs.Add(job);
        }

        if (jobs.Count == 0)
        {
            _log.LogDebug(
                "Missing poster sweep: no valid jobs built from actionable ids found={Found} totalCandidates={TotalCandidates} filteredByCooldown={FilteredByCooldown}",
                ids.Count,
                counts.TotalMissing,
                filteredByCooldown);
            return new MissingPosterSweepResult(ids.Count, 0, 0, 0, 0, 0);
        }

        var batch = await _queue.EnqueueManyAsync(jobs, ct, PosterFetchQueue.DefaultBatchEnqueueTimeout).ConfigureAwait(false);
        var result = new MissingPosterSweepResult(
            Found: ids.Count,
            Requested: jobs.Count,
            Enqueued: batch.Enqueued,
            Coalesced: batch.Coalesced,
            TimedOut: batch.TimedOut,
            Rejected: batch.Rejected);

        if (batch.TimedOut > 0)
        {
            _log.LogWarning(
                "Missing poster sweep: totalCandidates={TotalCandidates} filteredByCooldown={FilteredByCooldown} actionableFound={Found} requested={Requested} enqueued={Enqueued} coalesced={Coalesced} timedOut={TimedOut} rejected={Rejected}",
                counts.TotalMissing,
                filteredByCooldown,
                result.Found,
                result.Requested,
                result.Enqueued,
                result.Coalesced,
                result.TimedOut,
                result.Rejected);
        }
        else
        {
            _log.LogInformation(
                "Missing poster sweep: totalCandidates={TotalCandidates} filteredByCooldown={FilteredByCooldown} actionableFound={Found} requested={Requested} enqueued={Enqueued} coalesced={Coalesced} rejected={Rejected}",
                counts.TotalMissing,
                filteredByCooldown,
                result.Found,
                result.Requested,
                result.Enqueued,
                result.Coalesced,
                result.Rejected);
        }

        return result;
    }

    private async Task SweepOnceSafeAsync(CancellationToken ct)
    {
        try
        {
            await SweepOnceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Missing poster sweep run failed");
        }
    }
}
