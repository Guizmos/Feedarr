using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;

namespace Feedarr.Api.Services.Posters;

public sealed class PosterFetchWorkerPool : BackgroundService
{
    private readonly ILogger<PosterFetchWorkerPool> _log;
    private readonly IPosterFetchQueue _queue;
    private readonly IPosterFetchJobProcessor _processor;
    private readonly int _workerCount;

    public PosterFetchWorkerPool(
        ILogger<PosterFetchWorkerPool> log,
        IPosterFetchQueue queue,
        IPosterFetchJobProcessor processor,
        SettingsRepository settings)
    {
        _log = log;
        _queue = queue;
        _processor = processor;

        var maintenance = settings.GetMaintenance(new MaintenanceSettings { PosterWorkers = 1 });
        _workerCount = Math.Clamp(maintenance.PosterWorkers, 1, 2);
        _log.LogInformation("Poster workers: {WorkerCount} (maintenance snapshot)", _workerCount);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(1, _workerCount)
            .Select(workerId => RunWorkerLoopAsync(workerId, stoppingToken))
            .ToArray();

        return Task.WhenAll(workers);
    }

    private async Task RunWorkerLoopAsync(int workerId, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            PosterFetchJob currentJob;
            try
            {
                currentJob = await _queue.DequeueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            while (true)
            {
                PosterFetchProcessResult result;
                try
                {
                    result = await _processor.ProcessJobAsync(currentJob, workerId, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    result = new PosterFetchProcessResult(false);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Poster worker {WorkerId} loop error {ItemId}", workerId, currentJob.ItemId);
                    result = new PosterFetchProcessResult(false);
                }

                var followUp = _queue.Complete(currentJob, result);
                if (followUp is null)
                    break;

                currentJob = followUp;
            }
        }
    }
}
