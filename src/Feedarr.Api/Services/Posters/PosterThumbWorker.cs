namespace Feedarr.Api.Services.Posters;

public sealed class PosterThumbWorker : BackgroundService
{
    private readonly ILogger<PosterThumbWorker> _log;
    private readonly IPosterThumbQueue _queue;
    private readonly PosterFetchService _posters;

    public PosterThumbWorker(
        ILogger<PosterThumbWorker> log,
        IPosterThumbQueue queue,
        PosterFetchService posters)
    {
        _log = log;
        _queue = queue;
        _posters = posters;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            PosterThumbJob currentJob;
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
                await ProcessJobAsync(currentJob, stoppingToken).ConfigureAwait(false);
                var followUp = _queue.Complete(currentJob);
                if (followUp is null)
                    break;

                currentJob = followUp;
            }
        }
    }

    internal async Task<PosterThumbWorkResult> ProcessJobAsync(PosterThumbJob job, CancellationToken ct)
    {
        _log.LogInformation(
            "Poster thumb job start storeDir={StoreDir} reason={Reason} widths={Widths} releaseId={ReleaseId}",
            job.StoreDir,
            job.Reason,
            job.Widths is null || job.Widths.Count == 0 ? "standard" : string.Join(",", job.Widths),
            job.ReleaseId);

        try
        {
            var result = await _posters.EnsureThumbsAsync(job, ct).ConfigureAwait(false);
            if (result.Skipped)
            {
                _log.LogInformation(
                    "Poster thumb job skipped storeDir={StoreDir} reason={Reason}",
                    job.StoreDir,
                    result.Reason);
            }
            else if (result.Succeeded)
            {
                _log.LogInformation(
                    "Poster thumb job completed storeDir={StoreDir} generated={Generated} reason={Reason}",
                    job.StoreDir,
                    result.GeneratedWidths.Count == 0 ? "none" : string.Join(",", result.GeneratedWidths),
                    result.Reason);
            }
            else
            {
                _log.LogWarning(
                    "Poster thumb job failed storeDir={StoreDir} reason={Reason}",
                    job.StoreDir,
                    result.Reason);
            }

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Poster thumb job error storeDir={StoreDir} reason={Reason} releaseId={ReleaseId}",
                job.StoreDir,
                job.Reason,
                job.ReleaseId);
            return new PosterThumbWorkResult(false, false, "exception", []);
        }
    }
}
