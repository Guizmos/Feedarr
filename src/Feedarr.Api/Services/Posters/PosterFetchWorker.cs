using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Categories;

namespace Feedarr.Api.Services.Posters;

public sealed class PosterFetchWorker : BackgroundService
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RefreshTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxItemDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15)
    ];

    private readonly ILogger<PosterFetchWorker> _log;
    private readonly IPosterFetchQueue _queue;
    private readonly PosterFetchService _posters;
    private readonly ReleaseRepository _releases;
    private readonly RetroFetchLogService _retroLogs;
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;

    public PosterFetchWorker(
        ILogger<PosterFetchWorker> log,
        IPosterFetchQueue queue,
        PosterFetchService posters,
        ReleaseRepository releases,
        RetroFetchLogService retroLogs)
    {
        _log = log;
        _queue = queue;
        _posters = posters;
        _releases = releases;
        _retroLogs = retroLogs;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            PosterFetchJob job;
            try
            {
                job = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await ProcessJobAsync(job, stoppingToken);
        }
    }

    private async Task ProcessJobAsync(PosterFetchJob job, CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "Poster job start {ItemId} title={Title} year={Year} category={Category} force={Force} attempt={Attempt}",
            job.ItemId,
            job.Title,
            job.Year,
            job.Category,
            job.ForceRefresh,
            job.AttemptCount + 1);

        var release = _releases.GetForPoster(job.ItemId);
        if (release is null)
        {
            _log.LogWarning("Poster job skipped (release missing) {ItemId}", job.ItemId);
            return;
        }

        if (!job.ForceRefresh && ShouldSkipByTtl(release))
        {
            _log.LogInformation("Poster job skipped by TTL {ItemId}", job.ItemId);
            return;
        }

        var attempt = job.AttemptCount;
        var startedAt = DateTimeOffset.UtcNow;
        while (attempt < MaxAttempts)
        {
            attempt++;
            string? lastFailureOverride = null;

            await ApplyRateLimitAsync(stoppingToken);

            try
            {
                using var timeoutCts = new CancellationTokenSource(RequestTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);

                var res = await _posters.FetchPosterAsync(
                    job.ItemId,
                    linkedCts.Token,
                    logSingle: false,
                    skipIfExists: !job.ForceRefresh);

                if (res.Ok)
                {
                    _log.LogInformation(
                        "Poster job success {ItemId} status={Status}",
                        job.ItemId,
                        res.StatusCode);
                    return;
                }

                if (!ShouldRetry(res.StatusCode))
                {
                    _log.LogWarning(
                        "Poster job failed (no retry) {ItemId} status={Status}",
                        job.ItemId,
                        res.StatusCode);
                    LogRetroFailure(job, lastFailureOverride);
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _log.LogWarning("Poster job cancelled {ItemId}", job.ItemId);
                return;
            }
            catch (OperationCanceledException)
            {
                _log.LogWarning("Poster job timed out {ItemId} attempt={Attempt}", job.ItemId, attempt);
                PosterAudit.UpdateAttemptFailure(_releases, job.ItemId, null, null, null, null, "timeout");
                lastFailureOverride = "timeout";
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Poster job error {ItemId} attempt={Attempt}", job.ItemId, attempt);
                var error = $"exception: {ex.GetType().Name}: {ex.Message}";
                if (error.Length > 200)
                    error = error[..200];
                PosterAudit.UpdateAttemptFailure(_releases, job.ItemId, null, null, null, null, error);
                lastFailureOverride = error;
            }

            if (attempt >= MaxAttempts)
            {
                _log.LogError("Poster job failed after retries {ItemId}", job.ItemId);
                LogRetroFailure(job, lastFailureOverride);
                return;
            }

            if (DateTimeOffset.UtcNow - startedAt >= MaxItemDuration)
            {
                _log.LogWarning("Poster job time budget exceeded {ItemId}", job.ItemId);
                PosterAudit.UpdateAttemptFailure(_releases, job.ItemId, null, null, null, null, "time budget exceeded");
                LogRetroFailure(job, "time budget exceeded");
                return;
            }

            var delay = GetBackoffDelay(attempt);
            _log.LogInformation(
                "Poster job retrying {ItemId} attempt={NextAttempt} delayMs={DelayMs}",
                job.ItemId,
                attempt + 1,
                delay.TotalMilliseconds);

            await Task.Delay(delay, stoppingToken);
        }
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

    private async Task ApplyRateLimitAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextAllowed)
        {
            var delay = _nextAllowed - now;
            _log.LogInformation("Poster fetch rate limited; delaying {DelayMs}ms", delay.TotalMilliseconds);
            await Task.Delay(delay, ct);
        }

        _nextAllowed = DateTimeOffset.UtcNow + MinInterval;
    }

    private bool ShouldSkipByTtl(ReleaseForPoster release)
    {
        var posterFile = release.PosterFile;
        if (string.IsNullOrWhiteSpace(posterFile)) return false;

        if (release.PosterLastAttemptTs is null) return false;

        var lastAttemptTs = release.PosterLastAttemptTs.Value;
        var lastAttemptAt = DateTimeOffset.FromUnixTimeSeconds(lastAttemptTs);
        var threshold = DateTimeOffset.UtcNow - RefreshTtl;
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

    private static TimeSpan GetBackoffDelay(int attempt)
    {
        var index = Math.Clamp(attempt - 1, 0, RetryDelays.Length - 1);
        var jitterMs = Random.Shared.Next(200, 800);
        return RetryDelays[index] + TimeSpan.FromMilliseconds(jitterMs);
    }
}
