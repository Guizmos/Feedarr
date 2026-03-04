using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Categories;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Services.Posters;

public interface IPosterFetchJobProcessor
{
    Task<PosterFetchProcessResult> ProcessJobAsync(PosterFetchJob job, int workerId, CancellationToken stoppingToken);
}

public sealed class PosterFetchJobProcessor : IPosterFetchJobProcessor
{
    private readonly ILogger<PosterFetchJobProcessor> _log;
    private readonly IPosterFetchQueue _queue;
    private readonly PosterFetchService _posters;
    private readonly ReleaseRepository _releases;
    private readonly RetroFetchLogService _retroLogs;
    private readonly PosterFetchOptions _opt;

    public PosterFetchJobProcessor(
        ILogger<PosterFetchJobProcessor> log,
        IPosterFetchQueue queue,
        PosterFetchService posters,
        ReleaseRepository releases,
        RetroFetchLogService retroLogs,
        IOptions<PosterFetchOptions> opt)
    {
        _log = log;
        _queue = queue;
        _posters = posters;
        _releases = releases;
        _retroLogs = retroLogs;
        _opt = opt.Value;
    }

    public async Task<PosterFetchProcessResult> ProcessJobAsync(PosterFetchJob job, int workerId, CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "Poster worker {WorkerId} job start {ItemId} title={Title} year={Year} category={Category} force={Force} attempt={Attempt}",
            workerId,
            job.ItemId,
            job.Title,
            job.Year,
            job.Category,
            job.ForceRefresh,
            job.AttemptCount + 1);

        var release = _releases.GetForPoster(job.ItemId);
        if (release is null)
        {
            _log.LogWarning("Poster worker {WorkerId} job skipped (release missing) {ItemId}", workerId, job.ItemId);
            return new PosterFetchProcessResult(true);
        }

        var maxAttempts = Math.Max(1, _opt.MaxAttempts);
        var requestTimeout = TimeSpan.FromSeconds(Math.Max(1, _opt.RequestTimeoutSeconds));
        var maxItemDuration = TimeSpan.FromSeconds(Math.Max(1, _opt.MaxItemDurationSeconds));

        if (!job.ForceRefresh && ShouldSkipByTtl(release))
        {
            _log.LogInformation("Poster worker {WorkerId} job skipped by TTL {ItemId}", workerId, job.ItemId);
            return new PosterFetchProcessResult(true);
        }

        var attempt = job.AttemptCount;
        var startedAt = DateTimeOffset.UtcNow;
        while (attempt < maxAttempts)
        {
            attempt++;
            string? lastFailureOverride = null;

            try
            {
                using var timeoutCts = new CancellationTokenSource(requestTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);

                var res = await _posters.FetchPosterAsync(
                    job.ItemId,
                    linkedCts.Token,
                    logSingle: false,
                    skipIfExists: !job.ForceRefresh).ConfigureAwait(false);

                if (res.Ok)
                {
                    _log.LogInformation(
                        "Poster worker {WorkerId} job success {ItemId} status={Status}",
                        workerId,
                        job.ItemId,
                        res.StatusCode);
                    return new PosterFetchProcessResult(true);
                }

                if (!ShouldRetry(res.StatusCode))
                {
                    _log.LogWarning(
                        "Poster worker {WorkerId} job failed (no retry) {ItemId} status={Status}",
                        workerId,
                        job.ItemId,
                        res.StatusCode);
                    LogRetroFailure(job, lastFailureOverride);
                    return new PosterFetchProcessResult(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _log.LogWarning("Poster worker {WorkerId} job cancelled {ItemId}", workerId, job.ItemId);
                return new PosterFetchProcessResult(false);
            }
            catch (OperationCanceledException)
            {
                _log.LogWarning("Poster worker {WorkerId} job timed out {ItemId} attempt={Attempt}", workerId, job.ItemId, attempt);
                PosterAudit.UpdateAttemptFailure(_releases, job.ItemId, null, null, null, null, "timeout");
                lastFailureOverride = "timeout";
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Poster worker {WorkerId} job error {ItemId} attempt={Attempt}", workerId, job.ItemId, attempt);
                var error = $"exception: {ex.GetType().Name}: {ex.Message}";
                if (error.Length > 200)
                    error = error[..200];
                PosterAudit.UpdateAttemptFailure(_releases, job.ItemId, null, null, null, null, error);
                lastFailureOverride = error;
            }

            if (attempt >= maxAttempts)
            {
                _log.LogError("Poster worker {WorkerId} job failed after retries {ItemId}", workerId, job.ItemId);
                LogRetroFailure(job, lastFailureOverride);
                return new PosterFetchProcessResult(false);
            }

            if (DateTimeOffset.UtcNow - startedAt >= maxItemDuration)
            {
                _log.LogWarning("Poster worker {WorkerId} job time budget exceeded {ItemId}", workerId, job.ItemId);
                PosterAudit.UpdateAttemptFailure(_releases, job.ItemId, null, null, null, null, "time budget exceeded");
                LogRetroFailure(job, "time budget exceeded");
                return new PosterFetchProcessResult(false);
            }

            var delay = GetBackoffDelay(attempt);
            _log.LogInformation(
                "Poster worker {WorkerId} job retrying {ItemId} attempt={NextAttempt} delayMs={DelayMs}",
                workerId,
                job.ItemId,
                attempt + 1,
                delay.TotalMilliseconds);

            _queue.RecordRetry();
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _log.LogWarning("Poster worker {WorkerId} job cancelled during retry delay {ItemId}", workerId, job.ItemId);
                return new PosterFetchProcessResult(false);
            }
        }

        return new PosterFetchProcessResult(false);
    }

    private void LogRetroFailure(PosterFetchJob job, string? reasonOverride)
    {
        if (string.IsNullOrWhiteSpace(job.RetroLogFile)) return;

        var release = _releases.GetForPoster(job.ItemId);
        if (release is null) return;

        var category = release.CategoryName;
        if (string.IsNullOrWhiteSpace(category))
            category = release.UnifiedCategory;
        if (string.IsNullOrWhiteSpace(category))
            category = "unknown";

        var mediaType = release.MediaType;
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            var unifiedValue = release.UnifiedCategory;
            UnifiedCategoryMappings.TryParse(unifiedValue, out var unifiedCategory);
            mediaType = UnifiedCategoryMappings.ToMediaType(unifiedCategory);
        }
        if (string.IsNullOrWhiteSpace(mediaType))
            mediaType = "unknown";

        var provider = release.PosterProvider;
        var reason = reasonOverride ?? release.PosterLastError;
        if (string.IsNullOrWhiteSpace(reason))
            reason = "unknown";

        var query = job.Title;
        if (job.Year.HasValue)
            query = $"{query} ({job.Year.Value})";

        _retroLogs.AppendFailure(job.RetroLogFile!, new RetroFetchLogEntry(
            category,
            mediaType,
            provider,
            query,
            reason));
    }

    private bool ShouldSkipByTtl(ReleaseForPoster release)
    {
        var posterFile = release.PosterFile;
        if (string.IsNullOrWhiteSpace(posterFile)) return false;

        if (release.PosterLastAttemptTs is null) return false;

        var lastAttemptTs = release.PosterLastAttemptTs.Value;
        var lastAttemptAt = DateTimeOffset.FromUnixTimeSeconds(lastAttemptTs);
        var refreshTtl = TimeSpan.FromDays(Math.Max(1, _opt.RefreshTtlDays));
        var threshold = DateTimeOffset.UtcNow - refreshTtl;
        if (lastAttemptAt >= threshold) return false;

        var posterPath = Path.Combine(_posters.PostersDirPath, posterFile);
        return System.IO.File.Exists(posterPath);
    }

    private static bool ShouldRetry(int statusCode)
    {
        if (statusCode >= 500) return true;
        if (statusCode == 408 || statusCode == 429 || statusCode == 0) return true;
        return false;
    }

    private TimeSpan GetBackoffDelay(int attempt)
    {
        var delays = _opt.RetryDelaysSeconds;
        if (delays is null || delays.Length == 0)
            delays = [2, 5, 15];
        var index = Math.Clamp(attempt - 1, 0, delays.Length - 1);
        var baseSeconds = Math.Max(1, delays[index]);
        var jitterMs = Random.Shared.Next(200, 800);
        return TimeSpan.FromSeconds(baseSeconds) + TimeSpan.FromMilliseconds(jitterMs);
    }
}
